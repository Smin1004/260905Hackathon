using System;
using System.Collections;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;

/// <summary>
/// 네트워크 래퍼 싱글턴 (Docs/205 6장). Unity Multiplayer Services(Sessions + Relay) + Netcode for GameObjects 를 캡슐화한다.
/// 다른 씬 코드는 이 클래스의 메서드·이벤트만 사용하고 Unity.Services.* / Unity.Netcode.* 타입을 직접 쓰지 않는다.
///
/// 메시지 (NGO 이름 붙은 메시지, Host↔Client 1:1):
///   Hello       nickname + (호스트→클라이언트) 방 설정 4종            → OpponentReady
///   MapMeta     parTime, totalBytes, chunkCount                        (맵 전송 시작)
///   MapChunk    index, count, bytes(≤4KB)  — MapSerializer 페이로드   → 조립 완료 시 MapReceived
///   PlayResult  cleared, time, attempts, gaveUp                        → ResultReceived
/// 끊김: NGO 연결 해제 콜백 / 세션 제거 이벤트 → MatchAborted
/// </summary>
public class NetService : MonoBehaviour
{
    static NetService _instance;
    public static NetService Instance
    {
        get
        {
            if (_instance == null)
            {
                _instance = FindFirstObjectByType<NetService>();
                if (_instance == null) _instance = new GameObject("NetService").AddComponent<NetService>();
            }
            return _instance;
        }
    }

    const string MsgHello = "CJ_Hello";
    const string MsgMapMeta = "CJ_MapMeta";
    const string MsgMapChunk = "CJ_MapChunk";
    const string MsgPlayResult = "CJ_PlayResult";
    const string MsgNextRound = "CJ_NextRound";
    const string MsgSubmitFailed = "CJ_SubmitFailed";
    const string MsgVows = "CJ_Vows";
    const string MsgSettings = "CJ_Settings";   // 호스트 → 참가자: 방 설정 변경 (방 화면에서 실시간)
    const string MsgStart = "CJ_Start";         // 호스트 → 참가자: 게임 시작 (최종 설정 스냅샷 포함)
    const float LeaveTimeoutSeconds = 4f;
    const int MaxPlayers = 2;
    const float NetcodeStartTimeout = 20f;

    public bool IsInitialized { get; private set; }
    public bool InSession => _session != null;
    public bool IsHost => _session != null && _session.IsHost;
    public bool IsNetcodeUp => NetworkManager.Singleton != null && NetworkManager.Singleton.IsListening;
    /// <summary>상대와 Hello 교환까지 끝나 게임 메시지를 주고받을 수 있는 상태</summary>
    public bool IsOpponentReady { get; private set; }
    public string RoomCode => _session != null ? _session.Code : null;
    public string LocalNickname { get; private set; } = "플레이어";
    public string OpponentNickname { get; private set; } = "";
    /// <summary>방 설정 — 호스트가 정하고 Hello 로 클라이언트에 전달</summary>
    public RoomSettings Settings { get; private set; } = new RoomSettings();
    public string Status { get; private set; } = "";
    public string PlayerId => IsInitialized ? AuthenticationService.Instance.PlayerId : null;

    public event Action<string> StatusChanged;
    /// <summary>(상대 닉네임, 방 설정)</summary>
    public event Action<string, RoomSettings> OpponentReady;
    /// <summary>(받은 청크 수, 전체 청크 수)</summary>
    public event Action<int, int> MapChunkProgress;
    /// <summary>(상대 맵, 상대 맵의 패타임)</summary>
    public event Action<MapData, float> MapReceived;
    public event Action<PlayerRecord> ResultReceived;
    /// <summary>상대가 [다음 라운드] 준비 완료</summary>
    public event Action NextRoundReceived;
    /// <summary>상대가 그리기 시간 안에 맵을 제출하지 못함 (Docs/100 6장 제출 실패 → 패배)</summary>
    public event Action SubmitFailedReceived;
    /// <summary>상대가 고른 뜻 목록</summary>
    public event Action<System.Collections.Generic.List<VowId>> VowsReceived;
    /// <summary>사유 문구. 한 매치에 1회만 발생</summary>
    public event Action<string> MatchAborted;
    /// <summary>호스트가 방 설정을 바꿨다 (참가자 측에서 발생)</summary>
    public event Action<RoomSettings> SettingsReceived;
    /// <summary>호스트가 [게임 시작]을 눌렀다 (참가자 측에서 발생) — 최종 방 설정 포함</summary>
    public event Action<RoomSettings> StartReceived;

    ISession _session;
    ulong _peerClientId = ulong.MaxValue;
    bool _helloReceived;       // 상대의 Hello 를 받았는지
    Coroutine _helloLoop;      // 상대가 내 Hello 를 확인(ack)할 때까지 1초마다 재전송
    bool _aborted;
    Coroutine _netcodeWait;
    Action<ulong> _onConnected, _onDisconnected;
    Action _onTransportFailure;
    Action<bool> _onClientStopped, _onServerStopped;
    readonly MapChunkAssembler _assembler = new MapChunkAssembler();
    float _incomingParTime;
    int _incomingChunkCount;

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        if (_instance != null && _instance != this) { Destroy(gameObject); return; }
        _instance = this;
        DontDestroyOnLoad(gameObject);
        Application.runInBackground = true;
        EnsureNetworkManager();
    }

    void OnDestroy()
    {
        UnhookNetcode();
        if (_instance == this) _instance = null;
    }

    public async Task Init()
    {
        if (IsInitialized) return;
        SetStatus("Unity Services 초기화 중...");
        var options = new InitializationOptions();
        // 같은 PC 에서 여러 인스턴스를 띄워도 서로 다른 익명 계정이 되도록 프로필 분리
        options.SetProfile("cj" + UnityEngine.Random.Range(0, 1_000_000));
        await UnityServices.InitializeAsync(options);
        if (!AuthenticationService.Instance.IsSignedIn)
            await AuthenticationService.Instance.SignInAnonymouslyAsync();
        IsInitialized = true;
        SetStatus("서비스 준비 완료");
    }

    // ------------------------------------------------------------------ room

    public async Task<string> CreateRoom(RoomSettings settings, string nickname)
    {
        if (!IsInitialized) await Init();
        if (_session != null) throw new InvalidOperationException("already in a session");
        ResetMatchState();
        Settings = settings ?? new RoomSettings();
        LocalNickname = ClampNick(nickname, "플레이어1");

        SetStatus("방 생성 중 (Lobby + Relay 할당)...");
        var options = new SessionOptions { MaxPlayers = MaxPlayers, Name = "chojiilgwan" }.WithRelayNetwork();
        _session = await MultiplayerService.Instance.CreateSessionAsync(options);
        HookSession();
        _netcodeWait = StartCoroutine(WaitForNetcode());
        SetStatus($"방 코드 {_session.Code} — 상대를 기다립니다");
        return _session.Code;
    }

    public async Task JoinRoom(string roomCode, string nickname)
    {
        if (!IsInitialized) await Init();
        if (_session != null) throw new InvalidOperationException("already in a session");
        ResetMatchState();
        LocalNickname = ClampNick(nickname, "플레이어2");
        var code = (roomCode ?? "").Trim().ToUpperInvariant();
        if (code.Length == 0) throw new ArgumentException("방 코드를 입력하세요.");

        SetStatus($"방 참가 중 ({code})...");
        _session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);
        HookSession();
        _netcodeWait = StartCoroutine(WaitForNetcode());
        SetStatus("참가 완료 — 호스트와 연결 중");
    }

    public async Task Leave()
    {
        if (_netcodeWait != null) { StopCoroutine(_netcodeWait); _netcodeWait = null; }
        if (_helloLoop != null) { StopCoroutine(_helloLoop); _helloLoop = null; }
        UnhookNetcode();
        var s = _session;
        _session = null;
        if (s != null)
        {
            UnhookSession(s);
            try
            {
                // 상대가 먼저 나가 NGO 가 이미 멈춘 경우 SDK 의 LeaveAsync 가 정지 콜백을 기다리며 끝나지 않을 수 있다 → 타임아웃
                var leaveTask = s.LeaveAsync();
                var finished = await Task.WhenAny(leaveTask, Task.Delay(TimeSpan.FromSeconds(LeaveTimeoutSeconds)));
                if (finished != leaveTask) Debug.LogWarning("[NetService] LeaveAsync 타임아웃 — 로컬 정리만 진행");
                else if (leaveTask.IsFaulted) Debug.LogException(leaveTask.Exception);
            }
            catch (Exception e) { Debug.LogException(e); }
        }
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening) nm.Shutdown();
        ResetMatchState();
        SetStatus("세션을 나갔습니다.");
    }

    /// <summary>
    /// 상대가 나간 뒤 같은 방(코드 유지)에서 새 상대를 기다린다 — 호스트 전용. NGO 호스트가 살아 있어야 한다.
    /// </summary>
    public bool PrepareForNewOpponent()
    {
        var nm = NetworkManager.Singleton;
        if (_session == null || !_session.IsHost || nm == null || !nm.IsListening) return false;
        _peerClientId = ulong.MaxValue;
        _helloReceived = false;
        if (_helloLoop != null) { StopCoroutine(_helloLoop); _helloLoop = null; }
        _aborted = false;
        IsOpponentReady = false;
        OpponentNickname = "";
        _assembler.Reset();
        SetStatus($"방 코드 {_session.Code} — 새 상대를 기다립니다");
        return true;
    }

    /// <summary>같은 방에서 다음 라운드 시작 준비 — 매치 진행 플래그만 초기화 (세션·연결·상대 정보 유지).</summary>
    public void ResetForNextRound()
    {
        _assembler.Reset();
        _incomingParTime = 0f;
        _incomingChunkCount = 0;
    }

    static string ClampNick(string nick, string fallback)
    {
        var n = string.IsNullOrWhiteSpace(nick) ? fallback : nick.Trim();
        return n.Length > 16 ? n.Substring(0, 16) : n;   // Hello 는 비조각 전달(≤1264B) — 닉네임 길이 제한
    }

    void ResetMatchState()
    {
        _peerClientId = ulong.MaxValue;
        _helloReceived = false;
        if (_helloLoop != null) { StopCoroutine(_helloLoop); _helloLoop = null; }
        _aborted = false;
        IsOpponentReady = false;
        OpponentNickname = "";
        _assembler.Reset();
        _incomingParTime = 0f;
        _incomingChunkCount = 0;
    }

    // ------------------------------------------------------------------ session events

    void HookSession()
    {
        _session.PlayerJoined += OnSessionPlayerJoined;
        _session.PlayerLeaving += OnSessionPlayerLeaving;
        _session.RemovedFromSession += OnSessionRemoved;
        _session.Deleted += OnSessionDeleted;
    }

    void UnhookSession(ISession s)
    {
        s.PlayerJoined -= OnSessionPlayerJoined;
        s.PlayerLeaving -= OnSessionPlayerLeaving;
        s.RemovedFromSession -= OnSessionRemoved;
        s.Deleted -= OnSessionDeleted;
    }

    void OnSessionPlayerJoined(string playerId) => SetStatus("상대 참가 — 연결 대기");
    void OnSessionPlayerLeaving(string playerId)
    {
        if (_session != null && _session.IsHost && !_helloReceived) { ResetPeer("상대가 접속에 실패해 나갔습니다 — 다시 기다립니다."); return; }
        Abort("상대가 방을 나갔습니다.");
    }

    /// <summary>Hello 교환 전에 상대가 떨어진 경우(참가 실패 등): 방을 살려 두고 다음 접속을 기다린다 (호스트 전용).</summary>
    void ResetPeer(string status)
    {
        _peerClientId = ulong.MaxValue;
        if (_helloLoop != null) { StopCoroutine(_helloLoop); _helloLoop = null; }
        SetStatus(status);
    }
    void OnSessionRemoved() => Abort("세션에서 제거되었습니다 (호스트 종료).");
    void OnSessionDeleted() => Abort("세션이 삭제되었습니다.");

    // ------------------------------------------------------------------ netcode

    IEnumerator WaitForNetcode()
    {
        float t = 0f;
        while ((NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening) && t < NetcodeStartTimeout)
        {
            t += Time.deltaTime;
            yield return null;
        }
        _netcodeWait = null;
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
        {
            Abort($"네트워크 시작 실패: {NetcodeStartTimeout:0}초 안에 연결되지 않았습니다.");
            yield break;
        }

        var cmm = nm.CustomMessagingManager;
        cmm.RegisterNamedMessageHandler(MsgHello, OnHello);
        cmm.RegisterNamedMessageHandler(MsgMapMeta, OnMapMeta);
        cmm.RegisterNamedMessageHandler(MsgMapChunk, OnMapChunk);
        cmm.RegisterNamedMessageHandler(MsgPlayResult, OnPlayResult);
        cmm.RegisterNamedMessageHandler(MsgNextRound, OnNextRound);
        cmm.RegisterNamedMessageHandler(MsgSubmitFailed, OnSubmitFailed);
        cmm.RegisterNamedMessageHandler(MsgVows, OnVows);
        cmm.RegisterNamedMessageHandler(MsgSettings, OnSettings);
        cmm.RegisterNamedMessageHandler(MsgStart, OnStart);

        UnhookNetcode();
        _onConnected = OnClientConnected;
        _onDisconnected = OnClientDisconnected;
        _onTransportFailure = () => Abort("네트워크 전송 오류 (Relay 연결 끊김).");
        _onClientStopped = _ => { if (_session != null) Abort("네트워크가 종료되었습니다."); };
        _onServerStopped = _ => { if (_session != null) Abort("네트워크가 종료되었습니다."); };
        nm.OnClientConnectedCallback += _onConnected;
        nm.OnClientDisconnectCallback += _onDisconnected;
        nm.OnTransportFailure += _onTransportFailure;
        nm.OnClientStopped += _onClientStopped;
        nm.OnServerStopped += _onServerStopped;

        // 콜백을 걸기 전에 이미 연결이 끝난 경우 보정
        if (nm.IsServer)
        {
            foreach (var id in nm.ConnectedClientsIds)
                if (id != nm.LocalClientId) OnClientConnected(id);
        }
        else if (nm.IsConnectedClient)
        {
            OnClientConnected(nm.LocalClientId);
        }
        SetStatus(nm.IsHost ? "호스트 준비 완료 — 상대 접속 대기" : "호스트에 연결됨");
    }

    void UnhookNetcode()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        if (_onConnected != null) nm.OnClientConnectedCallback -= _onConnected;
        if (_onDisconnected != null) nm.OnClientDisconnectCallback -= _onDisconnected;
        if (_onTransportFailure != null) nm.OnTransportFailure -= _onTransportFailure;
        if (_onClientStopped != null) nm.OnClientStopped -= _onClientStopped;
        if (_onServerStopped != null) nm.OnServerStopped -= _onServerStopped;
        _onConnected = null;
        _onDisconnected = null;
        _onTransportFailure = null;
        _onClientStopped = null;
        _onServerStopped = null;
    }

    void OnClientConnected(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        if (nm.IsServer)
        {
            if (clientId == nm.LocalClientId) return;
            _peerClientId = clientId;
            StartHelloLoop();
        }
        else if (clientId == nm.LocalClientId)
        {
            StartHelloLoop();
        }
    }

    /// <summary>
    /// Hello 핸드셰이크. 상대가 아직 메시지 핸들러를 등록하기 전에 도착한 Hello 는 버려지므로(NGO 는 핸들러 없는 이름 붙은 메시지를 무시),
    /// 상대가 "너의 Hello 를 받았다"(ack) 고 응답할 때까지 1초마다 다시 보낸다.
    /// </summary>
    void StartHelloLoop()
    {
        if (_helloLoop != null || _helloReceived) return;
        _helloLoop = StartCoroutine(HelloLoop());
    }

    IEnumerator HelloLoop()
    {
        // 상대의 Hello 를 받을 때까지 재전송. 상대는 내 Hello 를 받으면 (ack=false 인 경우) 한 번 답장하므로 양쪽이 받으면 교신이 멎는다.
        int tries = 0;
        while (!_helloReceived && !_aborted && tries < 30)
        {
            try { SendHello(); }
            catch (Exception e) { Debug.LogException(e); }   // 전송 예외가 코루틴을 죽여 재시도가 멎지 않게
            tries++;
            yield return new WaitForSeconds(1f);
        }
        _helloLoop = null;
        if (!_helloReceived && !_aborted) Abort("상대와 초기 교신(Hello)에 실패했습니다.");
    }

    void OnClientDisconnected(ulong clientId)
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        if (nm.IsServer)
        {
            if (clientId != _peerClientId) return;
            if (!_helloReceived) { ResetPeer("상대가 접속 도중 끊겼습니다 — 다시 기다립니다."); return; }
            Abort("상대 연결이 끊겼습니다.");
        }
        else if (clientId == nm.LocalClientId) Abort("호스트와 연결이 끊겼습니다.");
    }

    // ------------------------------------------------------------------ messages: send

    void SendHello()
    {
        if (!CanSend()) return;
        var w = new FastBufferWriter(256, Allocator.Temp, 4096);
        using (w)
        {
            w.WriteValueSafe(LocalNickname);
            bool host = NetworkManager.Singleton.IsServer;
            w.WriteValueSafe(host);
            if (host)
            {
                w.WriteValueSafe(Settings.AttemptLimit);
                w.WriteValueSafe(Settings.DrawTimeLimit);
                w.WriteValueSafe(Settings.PlayTimeLimit);
                w.WriteValueSafe(Settings.VowPickCount);
                w.WriteValueSafe(Settings.VowCandidateCount);
            }
            w.WriteValueSafe(_helloReceived);   // ack: 나는 네 Hello 를 이미 받았다
            Send(MsgHello, w, NetworkDelivery.ReliableSequenced);
        }
    }

    /// <summary>맵 전송: MapMeta 1개 + 4KB 청크 N개 (Docs/205 5장). payload 는 MapSerializer.Serialize 결과.</summary>
    public void SendMap(byte[] payload, float parTime)
    {
        if (payload == null || payload.Length == 0) throw new ArgumentException("empty payload");
        if (!CanSend()) { Debug.LogWarning("[NetService] SendMap: 상대와 연결되지 않음"); return; }

        var chunks = MapChunker.Split(payload, MapConstants.NetworkChunkSize);
        var meta = new FastBufferWriter(64, Allocator.Temp, 256);
        using (meta)
        {
            meta.WriteValueSafe(parTime);
            meta.WriteValueSafe(payload.Length);
            meta.WriteValueSafe(chunks.Count);
            Send(MsgMapMeta, meta, NetworkDelivery.ReliableSequenced);
        }
        for (int i = 0; i < chunks.Count; i++)
        {
            var bytes = chunks[i];
            var w = new FastBufferWriter(bytes.Length + 64, Allocator.Temp, bytes.Length + 1024);
            using (w)
            {
                w.WriteValueSafe(i);
                w.WriteValueSafe(chunks.Count);
                w.WriteValueSafe(bytes.Length);
                w.WriteBytesSafe(bytes);
                Send(MsgMapChunk, w, NetworkDelivery.ReliableFragmentedSequenced);
            }
        }
        SetStatus($"맵 전송 완료 — {payload.Length / 1024f:0.0} KB, 청크 {chunks.Count}개");
    }

    public void SendResult(PlayerRecord r)
    {
        if (r == null) return;
        if (!CanSend()) { Debug.LogWarning("[NetService] SendResult: 상대와 연결되지 않음"); return; }
        var w = new FastBufferWriter(64, Allocator.Temp, 256);
        using (w)
        {
            w.WriteValueSafe(r.Cleared);
            w.WriteValueSafe(r.ClearTime);
            w.WriteValueSafe(r.AttemptsUsed);
            w.WriteValueSafe(r.GaveUp);
            Send(MsgPlayResult, w, NetworkDelivery.ReliableSequenced);
        }
    }

    /// <summary>방 설정 변경 통지 (호스트 전용, 방 화면). 상대가 아직 없으면 무시 — Hello 가 현재 값을 실어 보낸다.</summary>
    public void SendSettings(RoomSettings s)
    {
        if (s == null || NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
        Settings = s;
        if (!CanSend()) return;
        var w = new FastBufferWriter(32, Allocator.Temp, 128);
        using (w) { WriteSettings(w, s); Send(MsgSettings, w, NetworkDelivery.ReliableSequenced); }
    }

    /// <summary>게임 시작 (호스트 전용). 최종 설정 스냅샷을 함께 보낸다.</summary>
    public void SendStart(RoomSettings s)
    {
        if (NetworkManager.Singleton == null || !NetworkManager.Singleton.IsServer) return;
        if (s != null) Settings = s;
        if (!CanSend()) { Debug.LogWarning("[NetService] SendStart: 상대와 연결되지 않음"); return; }
        var w = new FastBufferWriter(32, Allocator.Temp, 128);
        using (w) { WriteSettings(w, Settings); Send(MsgStart, w, NetworkDelivery.ReliableSequenced); }
    }

    static void WriteSettings(FastBufferWriter w, RoomSettings s)
    {
        w.WriteValueSafe(s.AttemptLimit);
        w.WriteValueSafe(s.DrawTimeLimit);
        w.WriteValueSafe(s.PlayTimeLimit);
        w.WriteValueSafe(s.VowPickCount);
        w.WriteValueSafe(s.VowCandidateCount);
    }

    static RoomSettings ReadSettings(FastBufferReader reader)
    {
        var s = new RoomSettings();
        reader.ReadValueSafe(out s.AttemptLimit);
        reader.ReadValueSafe(out s.DrawTimeLimit);
        reader.ReadValueSafe(out s.PlayTimeLimit);
        reader.ReadValueSafe(out s.VowPickCount);
        reader.ReadValueSafe(out s.VowCandidateCount);
        return s;
    }

    public void SendNextRound()
    {
        if (!CanSend()) { Debug.LogWarning("[NetService] SendNextRound: 상대와 연결되지 않음"); return; }
        var w = new FastBufferWriter(16, Allocator.Temp, 64);
        using (w)
        {
            w.WriteValueSafe(1);
            Send(MsgNextRound, w, NetworkDelivery.ReliableSequenced);
        }
    }

    public void SendVows(System.Collections.Generic.IList<VowId> vows)
    {
        if (!CanSend()) { Debug.LogWarning("[NetService] SendVows: 상대와 연결되지 않음"); return; }
        var w = new FastBufferWriter(64, Allocator.Temp, 1024);
        using (w)
        {
            int n = vows == null ? 0 : vows.Count;
            w.WriteValueSafe(n);
            for (int i = 0; i < n; i++) w.WriteValueSafe((int)vows[i]);
            Send(MsgVows, w, NetworkDelivery.ReliableSequenced);
        }
    }

    void OnVows(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out int n);
        var list = new System.Collections.Generic.List<VowId>();
        for (int i = 0; i < n && i < 16; i++) { reader.ReadValueSafe(out int id); list.Add((VowId)id); }
        SetStatus("상대의 뜻 수신: " + VowCatalog.NamesOf(list));
        VowsReceived?.Invoke(list);
    }

    public void SendSubmitFailed()
    {
        if (!CanSend()) { Debug.LogWarning("[NetService] SendSubmitFailed: 상대와 연결되지 않음"); return; }
        var w = new FastBufferWriter(16, Allocator.Temp, 64);
        using (w)
        {
            w.WriteValueSafe(1);
            Send(MsgSubmitFailed, w, NetworkDelivery.ReliableSequenced);
        }
        SetStatus("그리기 시간 초과 — 제출 실패를 상대에게 알림");
    }

    void OnSubmitFailed(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out int _);
        SetStatus("상대가 그리기 시간 안에 맵을 제출하지 못했습니다");
        SubmitFailedReceived?.Invoke();
    }

    void OnNextRound(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out int _);
        SetStatus("상대가 다음 라운드를 준비했습니다");
        NextRoundReceived?.Invoke();
    }

    bool CanSend()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening) return false;
        if (nm.IsServer) return PeerId() != ulong.MaxValue;
        return nm.IsConnectedClient;
    }

    ulong PeerId()
    {
        var nm = NetworkManager.Singleton;
        if (!nm.IsServer) return NetworkManager.ServerClientId;
        if (_peerClientId == ulong.MaxValue)
            foreach (var id in nm.ConnectedClientsIds) if (id != nm.LocalClientId) { _peerClientId = id; break; }
        return _peerClientId;
    }

    void Send(string name, FastBufferWriter writer, NetworkDelivery delivery)
    {
        var nm = NetworkManager.Singleton;
        ulong target = PeerId();
        if (target == ulong.MaxValue) { Debug.LogWarning("[NetService] 보낼 상대가 없습니다: " + name); return; }
        nm.CustomMessagingManager.SendNamedMessage(name, target, writer, delivery);
    }

    // ------------------------------------------------------------------ messages: receive

    void OnHello(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out string nick);
        reader.ReadValueSafe(out bool fromHost);
        var s = new RoomSettings();
        if (fromHost)
        {
            reader.ReadValueSafe(out s.AttemptLimit);
            reader.ReadValueSafe(out s.DrawTimeLimit);
            reader.ReadValueSafe(out s.PlayTimeLimit);
            reader.ReadValueSafe(out s.VowPickCount);
            reader.ReadValueSafe(out s.VowCandidateCount);
        }
        reader.ReadValueSafe(out bool acked);

        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer && _peerClientId == ulong.MaxValue) _peerClientId = sender;

        bool first = !_helloReceived;
        _helloReceived = true;
        if (first)
        {
            if (fromHost) Settings = s;
            OpponentNickname = string.IsNullOrEmpty(nick) ? (fromHost ? "호스트" : "게스트") : nick;
            IsOpponentReady = true;
            SetStatus($"상대 연결 완료: {OpponentNickname}");
        }
        // 상대가 내 Hello 를 아직 못 받았으면 (또는 처음이면) 한 번 답장 — 양쪽이 서로 받으면 교신이 멎는다
        if (!acked) SendHello();
        if (first) OpponentReady?.Invoke(OpponentNickname, Settings);
    }

    void OnSettings(ulong sender, FastBufferReader reader)
    {
        var s = ReadSettings(reader);
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer) return;   // 설정은 호스트만 바꾼다
        Settings = s;
        SettingsReceived?.Invoke(s);
    }

    void OnStart(ulong sender, FastBufferReader reader)
    {
        var s = ReadSettings(reader);
        if (NetworkManager.Singleton != null && NetworkManager.Singleton.IsServer) return;
        Settings = s;
        SetStatus("호스트가 게임을 시작했습니다");
        StartReceived?.Invoke(s);
    }

    void OnMapMeta(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out _incomingParTime);
        reader.ReadValueSafe(out int totalBytes);
        reader.ReadValueSafe(out _incomingChunkCount);
        _assembler.Reset();
        SetStatus($"상대 맵 수신 시작 — {totalBytes / 1024f:0.0} KB, 청크 {_incomingChunkCount}개");
        MapChunkProgress?.Invoke(0, _incomingChunkCount);
    }

    void OnMapChunk(ulong sender, FastBufferReader reader)
    {
        reader.ReadValueSafe(out int index);
        reader.ReadValueSafe(out int count);
        reader.ReadValueSafe(out int len);
        var bytes = new byte[len];
        reader.ReadBytesSafe(ref bytes, len);

        bool complete = _assembler.Add(index, count, bytes);
        MapChunkProgress?.Invoke(_assembler.Received, count);
        if (!complete) return;

        MapData map;
        try
        {
            map = MapSerializer.Deserialize(_assembler.Assemble());
        }
        catch (Exception e)
        {
            Debug.LogException(e);
            Abort("상대 맵 데이터를 해석할 수 없습니다.");
            return;
        }
        _assembler.Reset();
        SetStatus($"상대 맵 수신 완료 — 스트로크 {map.Strokes.Count}, 점 {map.TotalPoints}, 패타임 {_incomingParTime:0.00}s");
        MapReceived?.Invoke(map, _incomingParTime);
    }

    void OnPlayResult(ulong sender, FastBufferReader reader)
    {
        var r = new PlayerRecord();
        reader.ReadValueSafe(out r.Cleared);
        reader.ReadValueSafe(out r.ClearTime);
        reader.ReadValueSafe(out r.AttemptsUsed);
        reader.ReadValueSafe(out r.GaveUp);
        SetStatus("상대 결과 수신");
        ResultReceived?.Invoke(r);
    }

    // ------------------------------------------------------------------ misc

    void Abort(string reason)
    {
        if (_aborted) return;
        _aborted = true;
        SetStatus("매치 중단: " + reason);
        MatchAborted?.Invoke(reason);
    }

    void SetStatus(string msg)
    {
        Status = msg;
        Debug.Log("[NetService] " + msg);
        StatusChanged?.Invoke(msg);
    }

    static void EnsureNetworkManager()
    {
        if (NetworkManager.Singleton != null || FindFirstObjectByType<NetworkManager>() != null) return;
        var go = new GameObject("NetworkManager (runtime)");
        var nm = go.AddComponent<NetworkManager>();
        var transport = go.AddComponent<UnityTransport>();
#if UNITY_WEBGL && !UNITY_EDITOR
        transport.UseWebSockets = true;   // 브라우저는 UDP 불가 — Relay WSS (SDK 가 WebGL 에서 자동으로 wss 할당을 고른다)
#endif
        nm.NetworkConfig = new NetworkConfig
        {
            NetworkTransport = transport,
            EnableSceneManagement = false,   // 씬 동기화 미사용 — 각자 로컬로 씬 전환 (Docs/205)
        };
    }
}
