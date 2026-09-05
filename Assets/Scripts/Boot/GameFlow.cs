using System;
using System.Collections;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

public enum MatchState { Lobby, RoomLobby, VowSelect, MapEdit, WaitingSubmit, ExchangePlay, WaitingResult, Result, WaitingNextRound, Aborted }

/// <summary>
/// 매치 흐름 FSM (Boot 씬, Docs/205 4장). Boot 씬은 항상 남고 MapEditor / Play 씬을 애디티브로 얹는다.
///
///   Lobby → (방 생성/참가) → RoomLobby (방 화면: 코드·플레이어·방 설정. Hello 교환 후 호스트 [게임 시작]) → VowSelect (각자 뜻 선택, 양쪽 확정·교환) → MapEdit
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
    /// <summary>이번 라운드에 제시된 뜻 후보 (VowSelect 상태에서 유효)</summary>
    public System.Collections.Generic.List<VowDef> VowCandidates { get; private set; } = new System.Collections.Generic.List<VowDef>();
    /// <summary>골라야 하는 뜻 개수 (방 설정)</summary>
    /// <summary>이 라운드에 고르는 뜻 개수 = 설정값 (+ 라운드−1, '라운드마다 뜻 +1' ON). 후보 수·전체 뜻 수를 넘지 않는다.</summary>
    public int VowPickCount
    {
        get
        {
            if (_data == null) return 1;
            var s = _data.Settings;
            int n = s.VowPickCount + (s.VowPickIncrement ? Mathf.Max(0, Round - 1) : 0);
            int cap = s.VowCandidateCount > 0 ? Mathf.Min(s.VowCandidateCount, VowCatalog.All.Count) : VowCatalog.All.Count;
            return Mathf.Clamp(n, 1, Mathf.Max(1, cap));
        }
    }
    /// <summary>방 설정의 그리기 시간 제한 (초). 0 이면 없음</summary>
    public float DrawTimeLimit => _data != null ? _data.Settings.DrawTimeLimit : 0f;
    /// <summary>이번 라운드 그리기 남은 시간 (초). 타이머가 없으면 -1. MapEditorHud 가 표시에 사용</summary>
    public float DrawTimeRemaining => _drawDeadline < 0f ? -1f : Mathf.Max(0f, _drawDeadline - Time.time);

    const string SceneMapEditor = "MapEditor";
    const string ScenePlay = "Play";

    NetService _net;
    MatchData _data;
    Camera _bootCamera;
    bool _mySubmitted, _opponentMapReceived, _myResultSent, _opponentResultReceived;
    bool _nextRoundMine, _nextRoundTheirs;
    bool _forfeitMine, _forfeitTheirs, _forfeitShown;
    bool _myVowsConfirmed, _opponentVowsReceived;
    int _myRoundWins, _theirRoundWins, _roundDraws;   // 매치 전적 (같은 방·같은 상대 동안 누적)
    int _tallyAppliedRound;                            // 이 라운드의 전적을 이미 반영했는지 (제출 실패 결과가 두 번 그려질 때 중복 방지)
    readonly System.Collections.Generic.List<VowId> _vowPicks = new System.Collections.Generic.List<VowId>();
    float _drawDeadline = -1f;   // Time.time 기준 그리기 마감. -1 = 타이머 없음
    bool _busy, _leaving, _editorLoadStarted, _transitioning;
    AsyncOperation _pendingLoad;   // 진행 중인 애디티브 로드 — 중단 시 완료를 기다려 정리

    // UI
    Canvas _canvas;
    GameObject _lobbyPanel, _waitPanel, _resultPanel, _roomPanel;
    Text _roomCodeBig, _roomHostText, _roomGuestText, _roomStatus, _roomHint;
    Button _startBtn, _copyCodeBtn;
    readonly System.Collections.Generic.List<SettingRow> _settingRows = new System.Collections.Generic.List<SettingRow>();
    class SettingRow { public string Name; public Text Value; public Button Prev, Next; public Func<int[]> Options; public Func<int> Get; public Action<int> Set; public Func<int, string> Format; }
    Text _lobbyStatus, _roomCodeText, _waitText, _resultTitle, _resultBody, _roomBarText, _scoreText;
    InputField _nickInput, _codeInput;
    Button _createBtn, _joinBtn, _lobbyLeaveBtn, _nextRoundBtn, _resultLeaveBtn, _waitNewBtn, _roomBarLeaveBtn;
    GameObject _roomBar;
    GameObject _vowPanel, _vowInfoPanel;
    Transform _vowCardRoot;
    Text _vowTitle, _vowHint, _vowInfoText;
    Button _vowConfirmBtn;
    readonly System.Collections.Generic.List<Button> _vowCardButtons = new System.Collections.Generic.List<Button>();

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
        _net.SubmitFailedReceived += OnOpponentForfeit;
        _net.VowsReceived += OnOpponentVows;
        _net.MatchAborted += OnMatchAborted;
        _net.SettingsReceived += OnSettingsReceived;
        _net.StartReceived += OnStartReceived;
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
            _net.SubmitFailedReceived -= OnOpponentForfeit;
            _net.VowsReceived -= OnOpponentVows;
            _net.MatchAborted -= OnMatchAborted;
            _net.SettingsReceived -= OnSettingsReceived;
            _net.StartReceived -= OnStartReceived;
        }
        PlayBootstrap.Finished -= OnMyPlayFinished;
        if (Instance == this) Instance = null;
    }

    void Update()
    {
        // 그리기 시간 만료 → 제출 실패 = 이번 라운드 패배 (Docs/100 6장). 이미 제출했으면 타이머 무관
        if (State == MatchState.MapEdit && !_mySubmitted && _drawDeadline > 0f && Time.time >= _drawDeadline) ForfeitByTimeout();
    }

    // ------------------------------------------------------------------ lobby actions (UI / AutoPilot)

    public async void CreateRoom()
    {
        if (_busy || _leaving || State != MatchState.Lobby) return;
        if (!HasNickname) { SetLobbyStatus("닉네임을 입력하세요."); return; }
        try
        {
            _busy = true; SetLobbyButtons(false);
            _data.MyNickname = Nickname = ReadNickname("플레이어1");
            string code = await _net.CreateRoom(_data.Settings, Nickname);
            if (this == null) return;
            SetState(MatchState.RoomLobby);
            ShowPanel(_roomPanel);
            SetRoomStatus("상대에게 방 코드를 알려주세요. 상대가 들어오면 [게임 시작]을 누를 수 있습니다.");
            RefreshRoomPanel();
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
        if (!HasNickname) { SetLobbyStatus("닉네임을 입력하세요."); return; }
        if (string.IsNullOrWhiteSpace(code)) { SetLobbyStatus("방 코드를 입력하세요."); return; }
        try
        {
            _busy = true; SetLobbyButtons(false);
            _data.MyNickname = Nickname = ReadNickname("플레이어2");
            await _net.JoinRoom(code, Nickname);
            if (this == null) return;
            SetState(MatchState.RoomLobby);
            ShowPanel(_roomPanel);
            SetRoomStatus("호스트와 연결 중...");
            RefreshRoomPanel();
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
        _data.MyVowHistory.Clear(); _data.OpponentVowHistory.Clear();   // 새 상대 = 새 매치 → 일관성 이력 초기화
        _myRoundWins = _theirRoundWins = _roundDraws = 0; _tallyAppliedRound = 0;
        Round = 1;
        LastError = "";
        SetState(MatchState.RoomLobby);
        ShowPanel(_roomPanel);
        SetRoomStatus("상대가 나갔습니다. 같은 방 코드로 새 상대를 기다립니다.");
        RefreshRoomPanel();
    }

    string ReadNickname(string fallback)
    {
        var n = _nickInput != null ? _nickInput.text.Trim() : Nickname;
        if (string.IsNullOrEmpty(n) || n == "플레이어") n = Nickname != "플레이어" ? Nickname : fallback;
        return n;
    }

    // ------------------------------------------------------------------ net events

    void OnNetStatus(string s)
    {
        if (State == MatchState.Lobby) SetLobbyStatus(s);
        else if (State == MatchState.RoomLobby) SetRoomStatus(s);
    }

    void OnOpponentReady(string nickname, RoomSettings settings)
    {
        _data.OpponentNickname = nickname;
        if (!_net.IsHost) _data.Settings = settings;   // 참가자는 호스트 값을 받는다. 호스트는 자기 값 유지 (같은 객체)
        if (_leaving || State == MatchState.Aborted) return;
        if (State == MatchState.RoomLobby || (State == MatchState.Lobby && _busy))
        {
            SetRoomStatus(_net.IsHost ? $"{nickname} 님이 들어왔습니다. 설정을 확인하고 [게임 시작]을 누르세요." : "연결 완료. 방장이 게임을 시작하면 뜻 선택으로 넘어갑니다.");
            RefreshRoomPanel();
        }
    }

    /// <summary>상대(참가자)가 연결되어 Hello 교환까지 끝났는가 — AutoPilot·방 화면 시작 버튼 조건</summary>
    public bool OpponentConnected => _net != null && _net.IsOpponentReady;

    /// <summary>[게임 시작] — 호스트 전용. 상대가 연결돼 있어야 한다. 최종 설정을 실어 보내고 양쪽이 뜻 선택으로.</summary>
    public bool StartGame()
    {
        if (State != MatchState.RoomLobby || _leaving || !_net.IsHost || !_net.IsOpponentReady || _editorLoadStarted) return false;
        _editorLoadStarted = true;   // 이 라운드의 진입은 한 번만
        _net.SendStart(_data.Settings);
        StartVowSelect();
        return true;
    }

    void OnStartReceived(RoomSettings settings)
    {
        if (_leaving || State != MatchState.RoomLobby || _editorLoadStarted) return;
        _data.Settings = settings;
        _editorLoadStarted = true;
        StartVowSelect();
    }

    void OnSettingsReceived(RoomSettings settings)
    {
        _data.Settings = settings;
        if (State == MatchState.RoomLobby) RefreshRoomPanel();
    }

    // ------------------------------------------------------------------ 뜻 선택 (Docs/100 4.1)

    void StartVowSelect()
    {
        SetState(MatchState.VowSelect);
        _myVowsConfirmed = false;
        _vowPicks.Clear();
        _data.MyVows.Clear();
        int candidateCount = _data.Settings.VowCandidateCount;
        VowCandidates = VowCatalog.RandomCandidates(candidateCount);
        BuildVowCards();
        ShowPanel(_vowPanel);
        _vowTitle.text = $"라운드 {Round} — 뜻을 {VowPickCount}개 고르세요";
        _vowHint.text = _opponentVowsReceived ? $"상대는 이미 골랐습니다: {VowCatalog.NamesOf(_data.OpponentVows)}" : "내가 고른 뜻은 상대 맵을 플레이할 때 나에게 걸립니다. 상대는 이 뜻을 보고 맵을 그립니다.";
        _vowConfirmBtn.interactable = false;
        _vowConfirmBtn.GetComponentInChildren<Text>().text = "확정";
    }

    /// <summary>후보 카드 토글 (UI). 선택 개수가 VowPickCount 가 되면 확정 가능.</summary>
    public void ToggleVowPick(VowId id)
    {
        if (State != MatchState.VowSelect || _myVowsConfirmed) return;
        if (_vowPicks.Contains(id)) _vowPicks.Remove(id);
        else
        {
            var others = new System.Collections.Generic.List<VowId>(_vowPicks);
            if (others.Count >= VowPickCount) others.RemoveAt(0);   // 꽉 찼으면 가장 먼저 고른 것을 교체
            var clash = VowCatalog.ConflictingName(others, id);
            if (clash != null)
            {
                // 조합 금지 (Docs/100 4.1): 저속↔과속, 고중력↔달 걷기 등
                _vowHint.text = $"{VowCatalog.NameOf(id)}은(는) {clash}와 함께 고를 수 없습니다.";
                return;
            }
            _vowPicks.Clear(); _vowPicks.AddRange(others); _vowPicks.Add(id);
        }
        RefreshVowCards();
    }

    /// <summary>뜻 확정 (UI·AutoPilot). picks 가 null 이면 현재 토글된 선택을 사용.</summary>
    public bool ConfirmVows(System.Collections.Generic.IList<VowId> picks = null)
    {
        if (State != MatchState.VowSelect || _myVowsConfirmed) return false;
        if (picks != null) { _vowPicks.Clear(); foreach (var v in picks) if (!_vowPicks.Contains(v) && VowCatalog.Get(v) != null) _vowPicks.Add(v); }
        if (_vowPicks.Count != VowPickCount) return false;
        if (!VowCatalog.IsValidSet(_vowPicks)) { _vowHint.text = "함께 고를 수 없는 뜻이 섞여 있습니다."; return false; }
        _myVowsConfirmed = true;
        Sound.Play(SfxId.Confirm);   // 확정됨
        _data.MyVows.Clear(); _data.MyVows.AddRange(_vowPicks);
        _data.MyVowHistory.Add(new System.Collections.Generic.List<VowId>(_data.MyVows));   // 라운드 이력 (일관성 계수)
        _net.SendVows(_data.MyVows);
        Debug.Log("[GameFlow] 내 뜻 확정: " + VowCatalog.NamesOf(_data.MyVows));
        _vowConfirmBtn.interactable = false;
        _vowConfirmBtn.GetComponentInChildren<Text>().text = "확정됨";
        _vowHint.text = _opponentVowsReceived ? $"상대의 뜻: {VowCatalog.NamesOf(_data.OpponentVows)} — 곧 시작합니다" : "확정 완료. 상대가 뜻을 고르는 중...";
        RefreshVowCards();
        TryFinishVowSelect();
        return true;
    }

    void OnOpponentVows(System.Collections.Generic.List<VowId> vows)
    {
        if (_leaving || State == MatchState.Aborted) return;
        _data.OpponentVows.Clear(); _data.OpponentVows.AddRange(vows);
        if (_opponentVowsReceived && _data.OpponentVowHistory.Count > 0) _data.OpponentVowHistory[_data.OpponentVowHistory.Count - 1] = new System.Collections.Generic.List<VowId>(vows);
        else _data.OpponentVowHistory.Add(new System.Collections.Generic.List<VowId>(vows));
        _opponentVowsReceived = true;
        Debug.Log("[GameFlow] 상대 뜻 수신: " + VowCatalog.NamesOf(vows));
        if (State == MatchState.VowSelect && _vowHint != null)
            _vowHint.text = _myVowsConfirmed ? $"상대의 뜻: {VowCatalog.NamesOf(vows)} — 곧 시작합니다" : $"상대는 이미 골랐습니다: {VowCatalog.NamesOf(vows)}";
        TryFinishVowSelect();
    }

    void TryFinishVowSelect()
    {
        if (State != MatchState.VowSelect || _transitioning || _leaving) return;
        if (!_myVowsConfirmed || !_opponentVowsReceived) return;
        StartCoroutine(LoadMapEditor());
    }

    void BuildVowCards()
    {
        foreach (var b in _vowCardButtons) if (b != null) Destroy(b.gameObject);
        _vowCardButtons.Clear();
        int n = VowCandidates.Count;
        if (n == 0) return;
        float gap = 0.015f, totalW = 0.9f, w = (totalW - gap * (n - 1)) / n, x0 = 0.05f;
        for (int i = 0; i < n; i++)
        {
            var d = VowCandidates[i];
            float x = x0 + i * (w + gap);
            var btn = RuntimeUI.Button(_vowCardRoot, new Vector2(x, 0f), new Vector2(x + w, 1f), "", () => ToggleVowPick(d.Id), new Color(0.22f, 0.24f, 0.30f), 20);
            var label = btn.GetComponentInChildren<Text>();
            label.alignment = TextAnchor.UpperCenter;
            label.text = $"\n{d.Name}\n\n<size=18>{d.Description}</size>\n\n<size=16>난이도 {new string('★', d.Tier)}</size>";
            label.supportRichText = true;
            _vowCardButtons.Add(btn);
        }
        RefreshVowCards();
    }

    void RefreshVowCards()
    {
        for (int i = 0; i < _vowCardButtons.Count && i < VowCandidates.Count; i++)
        {
            bool sel = _vowPicks.Contains(VowCandidates[i].Id);
            bool blocked = !sel && !VowCatalog.IsCompatible(_vowPicks, VowCandidates[i].Id);   // 현재 선택과 조합 금지 → 어둡게
            _vowCardButtons[i].GetComponent<Image>().color = sel ? new Color(0.25f, 0.55f, 0.95f) : blocked ? new Color(0.30f, 0.18f, 0.20f) : new Color(0.22f, 0.24f, 0.30f);
            _vowCardButtons[i].interactable = !_myVowsConfirmed;
        }
        if (_vowConfirmBtn != null && !_myVowsConfirmed) _vowConfirmBtn.interactable = _vowPicks.Count == VowPickCount;
    }

    void ShowVowInfo(bool visible)
    {
        if (_vowInfoPanel == null) return;
        _vowInfoPanel.SetActive(visible);
        if (visible && _vowInfoText != null)
            _vowInfoText.text = $"상대의 뜻\n<size=26><b>{VowCatalog.NamesOf(_data.OpponentVows)}</b></size>\n<size=15>상대는 이 제약을 지키며 내 맵을 플레이합니다. 검증도 이 뜻으로 합니다.</size>\n\n내 뜻\n<size=22>{VowCatalog.NamesOf(_data.MyVows)}</size>";
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
        Editor.VerificationChanged += OnEditorVerification;
        _drawDeadline = DrawTimeLimit > 0f ? Time.time + DrawTimeLimit : -1f;
        SetRoomBarVisible(true);
        ShowVowInfo(true);
        Debug.Log($"[GameFlow] 라운드 {Round} 맵 제작 시작 — 상대 {_data.OpponentNickname}, 그리기 {DrawTimeLimit}s, 플레이 {_data.Settings.PlayTimeLimit}s");
    }

    /// <summary>검증 플레이 중에는 PlaySession HUD 가 우상단을 쓰므로 방 나가기 버튼을 숨긴다 (겹침 방지)</summary>
    void OnEditorVerification(bool inVerification)
    {
        SetRoomBarVisible(!inVerification && State == MatchState.MapEdit);
        ShowVowInfo(!inVerification && State == MatchState.MapEdit);   // 검증 HUD 가 뜻을 직접 표시
    }

    void ForfeitByTimeout()
    {
        _drawDeadline = -1f;
        _forfeitMine = true;
        if (Editor != null) { if (Editor.InVerification) Editor.StopVerification(); Editor.SetLocked(true); }
        _net.SendSubmitFailed();
        Debug.Log("[GameFlow] 그리기 시간 초과 — 이번 라운드 패배");
        StartCoroutine(ShowForfeitResult());
    }

    void OnOpponentForfeit()
    {
        if (_leaving || State == MatchState.Aborted || State == MatchState.Lobby || State == MatchState.RoomLobby) return;
        if (State != MatchState.MapEdit && State != MatchState.WaitingSubmit && State != MatchState.Result) return;
        _forfeitTheirs = true;
        _drawDeadline = -1f;
        if (Editor != null) { if (Editor.InVerification) Editor.StopVerification(); Editor.SetLocked(true); }
        if (_forfeitShown) { RenderForfeitText(); return; }   // 양쪽 동시 초과 → 무승부로 갱신
        StartCoroutine(ShowForfeitResult());
    }

    IEnumerator ShowForfeitResult()
    {
        if (_forfeitShown) yield break;
        _forfeitShown = true;
        _transitioning = true;
        SetState(MatchState.Result);
        yield return UnloadContentScenes();
        _transitioning = false;
        if (_leaving) yield break;
        ShowPanel(_resultPanel);
        RenderForfeitText();
        _nextRoundBtn.gameObject.SetActive(true);
        _nextRoundBtn.interactable = true;
        _nextRoundBtn.GetComponentInChildren<Text>().text = "다음 라운드 (같은 방)";
        _waitNewBtn.gameObject.SetActive(false);
        _resultLeaveBtn.gameObject.SetActive(true);
        SetResultHint(_nextRoundTheirs ? "상대가 다음 라운드를 기다리고 있습니다" : "");
    }

    void RenderForfeitText()
    {
        if (_resultTitle == null) return;
        bool both = _forfeitMine && _forfeitTheirs;
        _resultTitle.text = both ? "무승부" : (_forfeitMine ? "패배" : "승리");
        ApplyTally(both ? Ranking.Outcome.Draw : (_forfeitMine ? Ranking.Outcome.Lose : Ranking.Outcome.Win), overwrite: true);
        if (_scoreText != null) _scoreText.text = $"<size=26>이번 라운드는 제출 실패로 판정</size>\n{TallyText()}";
        string who = both ? "양쪽 모두" : (_forfeitMine ? _data.MyNickname + " (나)" : _data.OpponentNickname);
        _resultBody.text = $"라운드 {Round}\n\n그리기 시간({DrawTimeLimit:0}초) 안에 맵을 제출하지 못함: {who}\n" +
                           (both ? "두 사람 모두 제출하지 못해 무승부입니다." : (_forfeitMine ? "제출 실패는 그 라운드 패배로 처리됩니다." : "상대의 제출 실패로 이 라운드는 승리입니다."));
        Debug.Log($"[GameFlow] 결과(제출 실패): {_resultTitle.text} — {who}");
    }

    /// <summary>라운드 결과를 전적에 1회 반영. overwrite = 같은 라운드에서 판정이 바뀐 경우(양쪽 동시 제출 실패 → 무승부) 갱신</summary>
    Ranking.Outcome _lastTallyOutcome;
    void ApplyTally(Ranking.Outcome outcome, bool overwrite = false)
    {
        if (_tallyAppliedRound == Round)
        {
            if (!overwrite || _lastTallyOutcome == outcome) return;
            switch (_lastTallyOutcome) { case Ranking.Outcome.Win: _myRoundWins--; break; case Ranking.Outcome.Lose: _theirRoundWins--; break; default: _roundDraws--; break; }
        }
        _tallyAppliedRound = Round;
        _lastTallyOutcome = outcome;
        switch (outcome) { case Ranking.Outcome.Win: _myRoundWins++; break; case Ranking.Outcome.Lose: _theirRoundWins++; break; default: _roundDraws++; break; }
    }

    string TallyText()
    {
        string draws = _roundDraws > 0 ? $"  (무 {_roundDraws})" : "";
        return $"전적  {_data.MyNickname} <b>{_myRoundWins}</b> : <b>{_theirRoundWins}</b> {_data.OpponentNickname}{draws}";
    }

    /// <summary>
    /// 라운드 점수 표시 (Docs/206 2장 패타임 빼기). 윗줄: 식을 작게 / 가운데: 최종 마진을 크게 / 아랫줄: 전적.
    /// 마진 = 클리어 시간 × 뜻 계수 − 상대 검증 시간(패타임). 낮을수록 좋고 음수면 제작자보다 빨랐다는 뜻.
    /// </summary>
    void SetScoreText(float myMult, float theirMult)
    {
        if (_scoreText == null) return;
        var s = _data.Settings;
        bool myClear = Ranking.IsCleared(_data.MyResult), theirClear = Ranking.IsCleared(_data.OpponentResult);
        float my = Ranking.Score(_data.MyResult, _data.OpponentParTime, s, myMult);
        float their = Ranking.Score(_data.OpponentResult, _data.MyParTime, s, theirMult);

        string myFormula = myClear
            ? $"{_data.MyResult.ClearTime:0.00}s × {myMult:0.00} − 검증 {_data.OpponentParTime:0.00}s"
            : $"미클리어 (시도 {(_data.MyResult != null ? _data.MyResult.AttemptsUsed : 0)})";
        string theirFormula = theirClear
            ? $"{_data.OpponentResult.ClearTime:0.00}s × {theirMult:0.00} − 검증 {_data.MyParTime:0.00}s"
            : $"미클리어 (시도 {(_data.OpponentResult != null ? _data.OpponentResult.AttemptsUsed : 0)})";

        bool myBetter = myClear && (!theirClear || my <= their);
        bool theirBetter = theirClear && (!myClear || their < my);
        string myBig = myClear ? Ranking.MarginText(my) : "미클리어";
        string theirBig = theirClear ? Ranking.MarginText(their) : "미클리어";
        if (myBetter) myBig = $"<color=#8CFFA6>{myBig}</color>";
        if (theirBetter) theirBig = $"<color=#8CFFA6>{theirBig}</color>";

        _scoreText.text =
            $"<size=18><color=#B8C0CC>{_data.MyNickname}: {myFormula}     |     {_data.OpponentNickname}: {theirFormula}</color></size>\n" +
            $"<size=52>{myBig}   <size=30>vs</size>   {theirBig}</size>\n" +
            $"<size=22>{TallyText()}   <color=#8A93A3>· 패타임 대비 마진, 낮을수록 좋음</color></size>";
    }

    void OnMyMapCompleted(MapData map, byte[] payload)
    {
        if (_mySubmitted || State != MatchState.MapEdit || _leaving) return;
        _drawDeadline = -1f;
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
        if (Editor != null) { Editor.Completed -= OnMyMapCompleted; Editor.VerificationChanged -= OnEditorVerification; Editor = null; }
        var me = SceneManager.GetSceneByName(SceneMapEditor);
        if (me.isLoaded) { var un = SceneManager.UnloadSceneAsync(me); while (!un.isDone) yield return null; }
        if (State == MatchState.Aborted || _leaving) { _transitioning = false; yield break; }
        ShowPanel(null);
        if (_roomBar != null) _roomBar.SetActive(false);   // 플레이 중에는 HUD 의 기권 버튼만
        ShowVowInfo(false);
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
        // 뜻 계수: 난이도 × 일관성 (Docs/206 2.5). 하드 강제된 뜻을 끝까지 유지한 보상 — 초지일관
        float myMult = VowCatalog.ScoreMultiplier(_data.MyVows, _data.MyVowHistory);
        float theirMult = VowCatalog.ScoreMultiplier(_data.OpponentVows, _data.OpponentVowHistory);
        int myStreak = VowCatalog.Streak(_data.MyVowHistory), theirStreak = VowCatalog.Streak(_data.OpponentVowHistory);
        var outcome = Ranking.Judge(_data.MyResult, _data.OpponentParTime, _data.OpponentResult, _data.MyParTime, s, myMult, theirMult);
        _resultTitle.text = Ranking.OutcomeText(outcome);
        ApplyTally(outcome);
        SetScoreText(myMult, theirMult);
        string vowLine =
            $"\n\n뜻 계수 — 나: 난이도 ×{VowCatalog.TierMultiplier(_data.MyVows):0.00} · 연속 {myStreak}라운드 ×{VowCatalog.ConsistencyCoefficient(myStreak):0.00} → 최종 {Ranking.AdjustedTime(_data.MyResult, s, myMult):0.00}s" +
            $"   |   상대: 난이도 ×{VowCatalog.TierMultiplier(_data.OpponentVows):0.00} · 연속 {theirStreak}라운드 ×{VowCatalog.ConsistencyCoefficient(theirStreak):0.00} → 최종 {Ranking.AdjustedTime(_data.OpponentResult, s, theirMult):0.00}s";

        // 양쪽 맵 썸네일 — 왼쪽: 내가 만든 맵(상대가 플레이), 오른쪽: 상대가 만든 맵(내가 플레이). 이전 라운드 것은 먼저 정리.
        ClearResultThumbnails();
        var palette = StrokePalette.LoadOrDefault();
        FillResultThumb(_myMapImage, _myMapCaption, ref _myMapSprite, _data.MyMap, palette,
            $"<b>{_data.MyNickname} (나)</b>가 만든 맵 · 패타임 {_data.MyParTime:0.00}s\n{_data.OpponentNickname}의 기록: {Ranking.RecordText(_data.OpponentResult, s)}");
        FillResultThumb(_oppMapImage, _oppMapCaption, ref _oppMapSprite, _data.OpponentMap, palette,
            $"<b>{_data.OpponentNickname}</b>가 만든 맵 · 패타임 {_data.OpponentParTime:0.00}s\n{_data.MyNickname} (나)의 기록: {Ranking.RecordText(_data.MyResult, s)}");
        _thumbRow.SetActive(_data.MyMap != null || _data.OpponentMap != null);

        _resultBody.text =
            $"라운드 {Round}\n" +
            $"{_data.MyNickname} (나) 뜻: {VowCatalog.NamesOf(_data.MyVows)}   |   {_data.OpponentNickname} 뜻: {VowCatalog.NamesOf(_data.OpponentVows)}" + vowLine;
        _nextRoundBtn.gameObject.SetActive(true);
        _nextRoundBtn.interactable = true;
        _nextRoundBtn.GetComponentInChildren<Text>().text = "다음 라운드 (같은 방)";
        _waitNewBtn.gameObject.SetActive(false);
        _resultLeaveBtn.gameObject.SetActive(true);
        string badge = myStreak >= 2 ? $"초지일관 — {myStreak}라운드 연속 같은 뜻을 지켰습니다" : "";
        SetResultHint(_nextRoundTheirs ? (badge.Length > 0 ? badge + "  ·  " : "") + "상대가 다음 라운드를 기다리고 있습니다" : badge);
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
        Debug.Log($"[GameFlow] 라운드 {Round} 시작 (같은 방) — 뜻 선택부터");
        _editorLoadStarted = true;
        StartVowSelect();
    }

    /// <summary>한 라운드의 진행 플래그·기록 초기화. 세션·상대 정보·방 설정은 유지.</summary>
    void ResetRound()
    {
        _mySubmitted = _opponentMapReceived = _myResultSent = _opponentResultReceived = false;
        _nextRoundMine = _nextRoundTheirs = false;
        _forfeitMine = _forfeitTheirs = _forfeitShown = false;
        _myVowsConfirmed = _opponentVowsReceived = false;
        _vowPicks.Clear();
        _data.MyVows.Clear(); _data.OpponentVows.Clear();
        _drawDeadline = -1f;
        _editorLoadStarted = false;
        _transitioning = false;
        if (Editor != null) { Editor.Completed -= OnMyMapCompleted; Editor.VerificationChanged -= OnEditorVerification; Editor = null; }
        _data.MyMap = null; _data.OpponentMap = null;
        _data.MyParTime = 0f; _data.OpponentParTime = 0f;
        _data.MyResult = null; _data.OpponentResult = null;
        SetResultHint("");
    }

    /// <summary>방을 나갈 때: 라운드 상태 + 상대 정보 초기화.</summary>
    void ResetAll()
    {
        ResetRound();
        _myRoundWins = _theirRoundWins = _roundDraws = 0; _tallyAppliedRound = 0;
        if (_scoreText != null) _scoreText.text = "";
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
        ShowVowInfo(false);
        SetBootCamera(true);
    }

    void SetState(MatchState s)
    {
        State = s;
        Debug.Log("[GameFlow] state → " + s);
        // 배경음: 교환 플레이(결과 대기 포함) = battle, 그 외(로비·뜻 선택·에디터·결과) = lobby_edit (Docs/102 3장)
        Sound.PlayMusic(s == MatchState.ExchangePlay || s == MatchState.WaitingResult ? MusicId.Battle : MusicId.LobbyEdit);
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
        RuntimeUI.Label(lp, new Vector2(0f, 0.80f), new Vector2(1f, 0.94f), "초지일관", 84, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        RuntimeUI.Label(lp, new Vector2(0f, 0.74f), new Vector2(1f, 0.80f), "스스로 걸은 뜻을 지킨 채, 상대가 그린 맵을 클리어하라", 24, TextAnchor.MiddleCenter, new Color(0.8f, 0.8f, 0.85f));

        // 카드 (방 화면의 패널과 같은 색) — 닉네임(필수) → 방 만들기 → 코드 참가
        var card = RuntimeUI.Panel(lp, new Vector2(0.33f, 0.28f), new Vector2(0.67f, 0.70f), new Color(0.14f, 0.16f, 0.21f));
        RuntimeUI.Label(card, new Vector2(0.08f, 0.86f), new Vector2(0.60f, 0.95f), "닉네임", 20, TextAnchor.MiddleLeft, Color.gray);
        _lobbyNickHint = RuntimeUI.Label(card, new Vector2(0.40f, 0.86f), new Vector2(0.92f, 0.95f), "필수 — 입력해야 방을 만들거나 참가할 수 있습니다", 16, TextAnchor.MiddleRight, new Color(0.97f, 0.37f, 0.30f));
        _nickInput = RuntimeUI.Input(card, new Vector2(0.08f, 0.72f), new Vector2(0.92f, 0.85f), "닉네임 (최대 12자)");
        _nickInput.characterLimit = 12;
        _nickInput.onValueChanged.AddListener(_ => RefreshLobbyButtons());
        WebPrompt.Attach(_nickInput, "닉네임을 입력하세요 (최대 12자)");   // WebGL: InputField 가 한글 IME 를 못 받아 브라우저 prompt 로 대신

        _createBtn = RuntimeUI.Button(card, new Vector2(0.08f, 0.50f), new Vector2(0.92f, 0.64f), "방 만들기", CreateRoom, new Color(0.25f, 0.55f, 0.95f), 30);

        RuntimeUI.Label(card, new Vector2(0.08f, 0.38f), new Vector2(0.92f, 0.46f), "또는 코드로 참가", 18, TextAnchor.MiddleCenter, Color.gray);
        _codeInput = RuntimeUI.Input(card, new Vector2(0.08f, 0.22f), new Vector2(0.60f, 0.36f), "방 코드 6자리");
        _codeInput.characterLimit = 8;
        _codeInput.onValueChanged.AddListener(_ => RefreshLobbyButtons());
        _joinBtn = RuntimeUI.Button(card, new Vector2(0.62f, 0.22f), new Vector2(0.92f, 0.36f), "참가", () => JoinRoom(_codeInput.text.Trim().ToUpperInvariant()), new Color(0.20f, 0.65f, 0.40f), 26);
        _lobbyHint = RuntimeUI.Label(card, new Vector2(0.08f, 0.06f), new Vector2(0.92f, 0.18f), "", 16, TextAnchor.MiddleCenter, new Color(0.75f, 0.78f, 0.85f));
        _createBtn.interactable = false; _joinBtn.interactable = false;

        _roomCodeText = null;   // 방 코드는 방 화면(_roomPanel)에서 표시
        _lobbyStatus = RuntimeUI.Label(lp, new Vector2(0.1f, 0.18f), new Vector2(0.9f, 0.26f), "Unity Services 초기화 중...", 22, TextAnchor.MiddleCenter, new Color(1f, 0.9f, 0.55f));
        _lobbyLeaveBtn = null;
        RefreshLobbyButtons();

        BuildRoomPanel(root);

        // ---- 대기 오버레이 (MapEditor / Play 위)
        _waitPanel = RuntimeUI.Panel(root, new Vector2(0.2f, 0.40f), new Vector2(0.8f, 0.60f), new Color(0.05f, 0.05f, 0.08f, 0.92f)).gameObject;
        _waitText = RuntimeUI.Label(_waitPanel.transform, new Vector2(0.03f, 0f), new Vector2(0.97f, 1f), "", 30, TextAnchor.MiddleCenter, Color.white);

        // ---- 방 나가기 (에디터 화면 위, 우상단 맨 위 띠) — MapEditorHud 의 타이머 패널(위에서 37px 아래부터)과 겹치지 않게 그 위에 둔다.
        //      검증 플레이 중에는 PlaySession HUD 가 우상단을 쓰므로 숨긴다 (OnEditorVerification)
        _roomBar = new GameObject("RoomBar", typeof(RectTransform));
        var rbRt = _roomBar.GetComponent<RectTransform>();
        rbRt.SetParent(root, false);
        rbRt.anchorMin = new Vector2(0.885f, 0.966f); rbRt.anchorMax = new Vector2(0.99f, 0.997f);
        rbRt.offsetMin = rbRt.offsetMax = Vector2.zero;
        _roomBarLeaveBtn = RuntimeUI.Button(_roomBar.transform, Vector2.zero, Vector2.one, "방 나가기", LeaveRoom, new Color(0.6f, 0.3f, 0.3f, 0.9f), 16);
        _roomBarText = null;
        _roomBar.SetActive(false);

        // ---- 뜻 선택 (Docs/204 2.1 뜻 선택 카드)
        _vowPanel = RuntimeUI.Panel(root, Vector2.zero, Vector2.one, new Color(0.10f, 0.11f, 0.14f)).gameObject;
        var vp = _vowPanel.transform;
        _vowTitle = RuntimeUI.Label(vp, new Vector2(0f, 0.82f), new Vector2(1f, 0.94f), "", 48, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        _vowHint = RuntimeUI.Label(vp, new Vector2(0.1f, 0.74f), new Vector2(0.9f, 0.82f), "", 22, TextAnchor.MiddleCenter, new Color(1f, 0.9f, 0.55f));
        _vowCardRoot = RuntimeUI.Rect("VowCards", vp, new Vector2(0f, 0.30f), new Vector2(1f, 0.70f), 0f);
        _vowConfirmBtn = RuntimeUI.Button(vp, new Vector2(0.38f, 0.14f), new Vector2(0.62f, 0.24f), "확정", () => ConfirmVows(), new Color(0.20f, 0.65f, 0.40f), 30);
        RuntimeUI.Button(vp, new Vector2(0.80f, 0.05f), new Vector2(0.95f, 0.11f), "방 나가기", LeaveRoom, new Color(0.6f, 0.3f, 0.3f), 20);

        // ---- 에디터 왼쪽 여백: 상대의 뜻 안내 (HUD 의 종이 영역 왼쪽 240px 여백 안)
        _vowInfoPanel = RuntimeUI.Panel(root, new Vector2(0.006f, 0.45f), new Vector2(0.118f, 0.80f), new Color(0.05f, 0.05f, 0.08f, 0.85f)).gameObject;
        _vowInfoText = RuntimeUI.Label(_vowInfoPanel.transform, new Vector2(0.06f, 0.04f), new Vector2(0.94f, 0.96f), "", 18, TextAnchor.UpperCenter, new Color(0.9f, 0.9f, 0.95f));
        _vowInfoText.supportRichText = true;
        _vowInfoPanel.SetActive(false);

        // ---- 결과 / 무효
        _resultPanel = RuntimeUI.Panel(root, Vector2.zero, Vector2.one, new Color(0.10f, 0.11f, 0.14f)).gameObject;
        var rp = _resultPanel.transform;
        _resultTitle = RuntimeUI.Label(rp, new Vector2(0f, 0.82f), new Vector2(1f, 0.96f), "", 88, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        _scoreText = RuntimeUI.Label(rp, new Vector2(0.03f, 0.66f), new Vector2(0.97f, 0.82f), "", 32, TextAnchor.MiddleCenter, new Color(0.92f, 0.94f, 0.98f), FontStyle.Bold);
        _scoreText.supportRichText = true;

        // 양쪽 맵 썸네일 (왼쪽: 내가 만든 맵 / 오른쪽: 상대가 만든 맵). 정상 결과(ShowResult)에서만 채우고 켠다.
        // Result(·다음 라운드 대기) 상태를 벗어나면(다음 라운드 시작·방 나가기·무효) 텍스처를 정리하고 숨긴다
        _thumbRow = RuntimeUI.Rect("MapThumbnails", rp, new Vector2(0.06f, 0.42f), new Vector2(0.94f, 0.65f), 0f).gameObject;
        _myMapImage = BuildResultThumb(_thumbRow.transform, new Vector2(0.00f, 0f), new Vector2(0.49f, 1f), out _myMapCaption);
        _oppMapImage = BuildResultThumb(_thumbRow.transform, new Vector2(0.51f, 0f), new Vector2(1.00f, 1f), out _oppMapCaption);
        _thumbRow.SetActive(false);
        StateChanged += st => { if (st != MatchState.Result && st != MatchState.WaitingNextRound) ClearResultThumbnails(); };   // [다음 라운드] 대기 중에는 유지

        _resultBody = RuntimeUI.Label(rp, new Vector2(0.08f, 0.29f), new Vector2(0.92f, 0.41f), "", 22, TextAnchor.MiddleCenter, new Color(0.9f, 0.9f, 0.95f));
        _nextRoundBtn = RuntimeUI.Button(rp, new Vector2(0.20f, 0.18f), new Vector2(0.48f, 0.28f), "다음 라운드 (같은 방)", RequestNextRound, new Color(0.20f, 0.65f, 0.40f), 28);
        _waitNewBtn = RuntimeUI.Button(rp, new Vector2(0.20f, 0.18f), new Vector2(0.48f, 0.28f), "같은 방에서 새 상대 기다리기", WaitForNewOpponent, new Color(0.25f, 0.55f, 0.95f), 24);
        _resultLeaveBtn = RuntimeUI.Button(rp, new Vector2(0.52f, 0.18f), new Vector2(0.80f, 0.28f), "방 나가기", LeaveRoom, new Color(0.6f, 0.3f, 0.3f), 28);
        _resultHint = RuntimeUI.Label(rp, new Vector2(0.1f, 0.10f), new Vector2(0.9f, 0.16f), "", 22, TextAnchor.MiddleCenter, new Color(1f, 0.9f, 0.55f));
        _waitNewBtn.gameObject.SetActive(false);

        ShowPanel(_lobbyPanel);
    }

    Text _resultHint;

    // ---- 결과 화면 맵 썸네일 (ShowResult / BuildUI 결과 패널 전용)
    GameObject _thumbRow;
    Image _myMapImage, _oppMapImage;
    Text _myMapCaption, _oppMapCaption;
    Sprite _myMapSprite, _oppMapSprite;
    const int ThumbWidth = 600, ThumbHeight = 300;   // 캔버스 30×15 비율

    /// <summary>썸네일 슬롯 1개: 종이색 프레임 + 비율 유지 Image + 아래 캡션 2줄.</summary>
    static Image BuildResultThumb(Transform parent, Vector2 aMin, Vector2 aMax, out Text caption)
    {
        var slot = RuntimeUI.Rect("Thumb", parent, aMin, aMax, 0f);
        var frame = RuntimeUI.Panel(slot, new Vector2(0f, 0.30f), new Vector2(1f, 1f), new Color(0.05f, 0.05f, 0.08f, 0.6f));
        var imgRt = RuntimeUI.Rect("Image", frame, Vector2.zero, Vector2.one, 8f);
        var img = imgRt.gameObject.AddComponent<Image>();
        img.preserveAspect = true;
        img.raycastTarget = false;
        img.color = Color.white;
        caption = RuntimeUI.Label(slot, new Vector2(0f, 0f), new Vector2(1f, 0.28f), "", 22, TextAnchor.UpperCenter, new Color(0.9f, 0.9f, 0.95f));
        caption.supportRichText = true;
        return img;
    }

    /// <summary>한쪽 썸네일 채우기. map 이 null(제출 실패 등)이면 그 슬롯을 숨긴다.</summary>
    void FillResultThumb(Image img, Text caption, ref Sprite sprite, MapData map, StrokePalette palette, string captionText)
    {
        MapThumbnail.Release(ref sprite);
        bool has = map != null;
        img.transform.parent.gameObject.SetActive(has);
        caption.gameObject.SetActive(has);
        if (!has) return;
        sprite = MapThumbnail.RenderSprite(map, palette, ThumbWidth, ThumbHeight);
        img.sprite = sprite;
        caption.text = captionText;
    }

    /// <summary>썸네일 텍스처·스프라이트 파괴 + 숨김. ShowResult 진입 시와 Result 상태를 벗어날 때 호출.</summary>
    void ClearResultThumbnails()
    {
        if (_myMapImage != null) _myMapImage.sprite = null;
        if (_oppMapImage != null) _oppMapImage.sprite = null;
        MapThumbnail.Release(ref _myMapSprite);
        MapThumbnail.Release(ref _oppMapSprite);
        if (_thumbRow != null) _thumbRow.SetActive(false);
    }

    // ------------------------------------------------------------------ 방 화면 (Docs/204 2.1b) — 코드·플레이어·방 설정·시작

    static readonly int[] AttemptOptions = { 0, 3, 5 };
    static readonly int[] DrawTimeOptions = { 120, 300, 600 };
    static readonly int[] PlayTimeOptions = { 120, 180, 300 };
    static readonly int[] PickOptions = { 1, 2, 3 };
    static readonly int[] CandidateOptions = { 3, 5, 8, 0 };
    static readonly int[] ToggleOptions = { 0, 1 };

    void BuildRoomPanel(Transform root)
    {
        _roomPanel = RuntimeUI.Panel(root, Vector2.zero, Vector2.one, new Color(0.10f, 0.11f, 0.14f)).gameObject;
        var rp = _roomPanel.transform;
        RuntimeUI.Label(rp, new Vector2(0f, 0.90f), new Vector2(1f, 0.97f), "방", 30, TextAnchor.MiddleCenter, new Color(0.8f, 0.8f, 0.85f));
        _roomCodeBig = RuntimeUI.Label(rp, new Vector2(0f, 0.80f), new Vector2(1f, 0.91f), "", 72, TextAnchor.MiddleCenter, new Color(0.5f, 1f, 0.6f), FontStyle.Bold);
        _copyCodeBtn = RuntimeUI.Button(rp, new Vector2(0.76f, 0.83f), new Vector2(0.85f, 0.88f), "코드 복사", CopyRoomCode, new Color(0.25f, 0.30f, 0.40f), 18);

        // 플레이어 2칸
        var players = RuntimeUI.Panel(rp, new Vector2(0.08f, 0.62f), new Vector2(0.46f, 0.77f), new Color(0.14f, 0.16f, 0.21f));
        RuntimeUI.Label(players, new Vector2(0.05f, 0.70f), new Vector2(0.95f, 0.98f), "플레이어", 20, TextAnchor.MiddleLeft, Color.gray);
        _roomHostText = RuntimeUI.Label(players, new Vector2(0.05f, 0.38f), new Vector2(0.95f, 0.68f), "", 26, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);
        _roomGuestText = RuntimeUI.Label(players, new Vector2(0.05f, 0.05f), new Vector2(0.95f, 0.36f), "", 26, TextAnchor.MiddleLeft, Color.white, FontStyle.Bold);

        // 방 설정 6종 (Docs/100 7.1) — 호스트만 조작, 참가자는 표시만
        var settings = RuntimeUI.Panel(rp, new Vector2(0.50f, 0.30f), new Vector2(0.92f, 0.77f), new Color(0.14f, 0.16f, 0.21f));
        RuntimeUI.Label(settings, new Vector2(0.05f, 0.88f), new Vector2(0.95f, 0.98f), "방 설정", 20, TextAnchor.MiddleLeft, Color.gray);
        _settingRows.Clear();
        AddSettingRow(settings, 0, "시도 제한", () => AttemptOptions, () => _data.Settings.AttemptLimit, v => _data.Settings.AttemptLimit = v, v => v == 0 ? "무한" : $"{v}회");
        AddSettingRow(settings, 1, "그리기 시간", () => DrawTimeOptions, () => _data.Settings.DrawTimeLimit, v => _data.Settings.DrawTimeLimit = v, v => $"{v / 60}분");
        AddSettingRow(settings, 2, "플레이 시간", () => PlayTimeOptions, () => _data.Settings.PlayTimeLimit, v => _data.Settings.PlayTimeLimit = v, v => $"{v / 60}분");
        AddSettingRow(settings, 3, "뜻 개수", () => PickOptions, () => _data.Settings.VowPickCount, v => _data.Settings.VowPickCount = v, v => $"{v}개");
        AddSettingRow(settings, 4, "뜻 후보 수", () => CandidateOptions, () => _data.Settings.VowCandidateCount, v => _data.Settings.VowCandidateCount = v, v => v == 0 ? "전체" : $"{v}개");
        AddSettingRow(settings, 5, "라운드마다 뜻 +1", () => ToggleOptions, () => _data.Settings.VowPickIncrement ? 1 : 0, v => _data.Settings.VowPickIncrement = v == 1, v => v == 1 ? "ON" : "OFF");
        _roomHint = RuntimeUI.Label(rp, new Vector2(0.08f, 0.30f), new Vector2(0.46f, 0.60f), "", 20, TextAnchor.UpperLeft, new Color(0.75f, 0.78f, 0.85f));

        _roomStatus = RuntimeUI.Label(rp, new Vector2(0.08f, 0.20f), new Vector2(0.92f, 0.28f), "", 22, TextAnchor.MiddleCenter, new Color(1f, 0.9f, 0.55f));
        _startBtn = RuntimeUI.Button(rp, new Vector2(0.30f, 0.08f), new Vector2(0.58f, 0.17f), "게임 시작", () => StartGame(), new Color(0.20f, 0.65f, 0.40f), 30);
        RuntimeUI.Button(rp, new Vector2(0.60f, 0.08f), new Vector2(0.70f, 0.17f), "방 나가기", LeaveRoom, new Color(0.6f, 0.3f, 0.3f), 22);
        _roomPanel.SetActive(false);
    }

    void AddSettingRow(Transform parent, int index, string name, Func<int[]> options, Func<int> get, Action<int> set, Func<int, string> format)
    {
        float top = 0.86f - index * 0.135f, bottom = top - 0.115f;   // 6행
        RuntimeUI.Label(parent, new Vector2(0.05f, bottom), new Vector2(0.45f, top), name, 24, TextAnchor.MiddleLeft, Color.white);
        var row = new SettingRow { Name = name, Options = options, Get = get, Set = set, Format = format };
        row.Prev = RuntimeUI.Button(parent, new Vector2(0.50f, bottom + 0.015f), new Vector2(0.58f, top - 0.015f), "<", () => CycleSetting(row, -1), new Color(0.25f, 0.30f, 0.40f), 22);
        row.Value = RuntimeUI.Label(parent, new Vector2(0.59f, bottom), new Vector2(0.84f, top), "", 26, TextAnchor.MiddleCenter, new Color(0.5f, 1f, 0.6f), FontStyle.Bold);
        row.Next = RuntimeUI.Button(parent, new Vector2(0.85f, bottom + 0.015f), new Vector2(0.93f, top - 0.015f), ">", () => CycleSetting(row, +1), new Color(0.25f, 0.30f, 0.40f), 22);
        _settingRows.Add(row);
    }

    void CycleSetting(SettingRow row, int dir)
    {
        if (State != MatchState.RoomLobby || !_net.IsHost) return;
        var opts = row.Options();
        int cur = row.Get(), idx = Array.IndexOf(opts, cur);
        if (idx < 0) idx = 0; else idx = (idx + dir + opts.Length) % opts.Length;
        row.Set(opts[idx]);
        // 뜻 개수는 후보 수를 넘을 수 없다 (후보 0 = 전체 8개)
        int cand = _data.Settings.VowCandidateCount == 0 ? VowCatalog.All.Count : _data.Settings.VowCandidateCount;
        if (_data.Settings.VowPickCount > cand) _data.Settings.VowPickCount = cand;
        _net.SendSettings(_data.Settings);
        RefreshRoomPanel();
    }

    void CopyRoomCode()
    {
        var code = _net != null ? _net.RoomCode : null;
        if (string.IsNullOrEmpty(code)) return;
        bool ok = Clipboard.Copy(code);   // WebGL 포함 (Scripts/Common/Clipboard.cs)
        SetRoomStatus(ok ? $"방 코드 {code} 를 복사했습니다." : $"복사에 실패했습니다. 코드를 직접 알려주세요: {code}");
    }

    /// <summary>방 화면의 코드·플레이어·설정·버튼 상태를 현재 데이터로 다시 그린다.</summary>
    void RefreshRoomPanel()
    {
        if (_roomPanel == null) return;
        bool host = _net != null && _net.IsHost;
        bool opp = OpponentConnected;
        string code = _net != null ? _net.RoomCode : null;
        _roomCodeBig.text = string.IsNullOrEmpty(code) ? "..." : $"방 코드  {code}";
        string me = _data.MyNickname, them = opp && !string.IsNullOrEmpty(_data.OpponentNickname) ? _data.OpponentNickname : null;
        string waiting = "<color=#8A93A3>대기 중...</color>";
        _roomHostText.text = "방장  " + (host ? $"{me} <color=#8CFFA6>(나)</color>" : (them ?? waiting));
        _roomGuestText.text = "참가자  " + (host ? (them ?? waiting) : $"{me} <color=#8CFFA6>(나)</color>");
        _roomHostText.supportRichText = _roomGuestText.supportRichText = true;
        foreach (var r in _settingRows)
        {
            r.Value.text = r.Format(r.Get());
            r.Prev.gameObject.SetActive(host); r.Next.gameObject.SetActive(host);
        }
        _roomHint.text = host
            ? "설정은 방장만 바꿀 수 있고 참가자 화면에 바로 반영됩니다.\n\n• 시도 제한·플레이 시간: 교환 플레이에만 적용\n• 그리기 시간: 검증 플레이까지 포함한 라운드 제작 시간\n• 뜻 개수·후보 수: 라운드마다 각자 고르는 뜻\n• 라운드마다 뜻 +1: 2라운드 2개, 3라운드 3개… (후보 수까지)"
            : "방장이 설정을 정하고 [게임 시작]을 누르면 뜻 선택으로 넘어갑니다.\n\n• 시도 제한·플레이 시간: 교환 플레이에만 적용\n• 그리기 시간: 검증 플레이까지 포함한 라운드 제작 시간";
        _startBtn.gameObject.SetActive(host);
        _startBtn.interactable = host && opp && State == MatchState.RoomLobby;
        _startBtn.GetComponentInChildren<Text>().text = opp ? "게임 시작" : "상대를 기다리는 중...";
    }

    void ShowPanel(GameObject panel)
    {
        _lobbyPanel.SetActive(panel == _lobbyPanel);
        _waitPanel.SetActive(panel == _waitPanel);
        _resultPanel.SetActive(panel == _resultPanel);
        if (_vowPanel != null) _vowPanel.SetActive(panel == _vowPanel);
        if (_roomPanel != null) _roomPanel.SetActive(panel == _roomPanel);
    }

    void SetRoomStatus(string s) { if (_roomStatus != null) _roomStatus.text = s; }

    void SetRoomBarVisible(bool visible)
    {
        if (_roomBar != null) _roomBar.SetActive(visible);
    }

    void SetLobbyStatus(string s) { if (_lobbyStatus != null) _lobbyStatus.text = s; }
    void SetWaitText(string s) { if (_waitText != null) _waitText.text = s; }
    void SetResultHint(string s) { if (_resultHint != null) _resultHint.text = s; }
    bool _lobbyButtonsOn = true;
    Text _lobbyNickHint, _lobbyHint;

    /// <summary>입력창에 닉네임이 있는가 (AutoPilot 처럼 코드로 Nickname 을 넣은 경우도 인정)</summary>
    bool HasNickname => (_nickInput != null && !string.IsNullOrWhiteSpace(_nickInput.text)) || (!string.IsNullOrWhiteSpace(Nickname) && Nickname != "플레이어");
    bool HasRoomCode => _codeInput != null && _codeInput.text.Trim().Length >= 4;

    void SetLobbyButtons(bool on) { _lobbyButtonsOn = on; RefreshLobbyButtons(); }

    /// <summary>닉네임 미입력 시 방 만들기·참가 불가, 코드 미입력 시 참가 불가. 안내 문구도 함께 갱신.</summary>
    void RefreshLobbyButtons()
    {
        if (_createBtn == null || _joinBtn == null) return;
        bool nick = HasNickname;
        _createBtn.interactable = _lobbyButtonsOn && NetReady && nick;
        _joinBtn.interactable = _lobbyButtonsOn && NetReady && nick && HasRoomCode;
        if (_lobbyNickHint != null) _lobbyNickHint.gameObject.SetActive(!nick);
        if (_lobbyHint != null)
            _lobbyHint.text = !NetReady ? "네트워크 준비 중..." : !nick ? "닉네임을 먼저 입력하세요" : !HasRoomCode ? "방을 새로 만들거나, 받은 방 코드를 입력해 참가하세요" : "";
    }

    /// <summary>방 생성·참가 뒤에는 입력 위젯(닉네임·방 만들기·코드·참가)을 숨기고 방 코드와 상태만 남긴다.</summary>
    void SetLobbyInputsVisible(bool visible)
    {
        if (_nickInput != null) _nickInput.gameObject.SetActive(visible);
        if (_createBtn != null) _createBtn.gameObject.SetActive(visible);
        if (_codeInput != null) _codeInput.gameObject.SetActive(visible);
        if (_joinBtn != null) _joinBtn.gameObject.SetActive(visible);
    }

    static void EnsureEventSystem()
    {
        if (UnityEngine.EventSystems.EventSystem.current != null || FindFirstObjectByType<UnityEngine.EventSystems.EventSystem>() != null) return;
        var go = new GameObject("EventSystem (runtime)");
        go.AddComponent<UnityEngine.EventSystems.EventSystem>();
        go.AddComponent<UnityEngine.InputSystem.UI.InputSystemUIInputModule>();
    }
}
