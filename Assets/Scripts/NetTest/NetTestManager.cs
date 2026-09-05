using System;
using System.Collections;
using Unity.Collections;
using Unity.Netcode;
using Unity.Netcode.Transports.UTP;
using Unity.Services.Authentication;
using Unity.Services.Core;
using Unity.Services.Multiplayer;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.InputSystem.UI;
using UnityEngine.UI;

/// <summary>
/// 멀티플레이 검증 전용 씬 (Assets/Scenes/NetTest.unity).
/// Docs/205_network.md 의 백엔드(Unity Multiplayer Services Sessions + Relay + NGO)가 실제로 동작하는지 확인한다.
///
/// 씬에는 이 컴포넌트 하나만 있으면 된다 — UI, EventSystem, NetworkManager 를 런타임에 전부 생성한다
/// (씬 배선 실수로 검증이 실패하는 일을 막기 위해 의도적으로 코드에서 만든다).
///
/// 흐름: 서비스 초기화(익명 로그인) → [방 만들기] 또는 [코드 참가] → NGO 연결 대기 →
///       텍스트 입력 후 [보내기] → Player 1(Host)의 텍스트는 왼쪽, Player 2(Client)의 텍스트는 오른쪽에 표시.
/// 게임 코드가 아니므로 Boot/Lobby 등 실제 씬에서는 참조하지 않는다.
/// </summary>
public class NetTestManager : MonoBehaviour
{
    const string MsgName = "NetTest_Text";
    const int MaxPlayers = 2;
    const float NetcodeStartTimeout = 20f;

    ISession _session;
    int _myIndex;                    // 1 = Host(왼쪽), 2 = Client(오른쪽)
    bool _busy;
    bool _connected;                 // NGO 연결 완료 여부 (자동 테스트용)

    Action<ulong> _onConnected, _onDisconnected;

    Font _font;
    Text _status, _codeText, _leftHeader, _rightHeader, _leftText, _rightText;
    InputField _joinInput, _chatInput;
    Button _createBtn, _joinBtn, _leaveBtn, _sendBtn;

    // ------------------------------------------------------------------ lifecycle

    void Awake()
    {
        Application.runInBackground = true;   // 두 창을 동시에 띄워도 둘 다 동작해야 함
        _font = Resources.GetBuiltinResource<Font>("LegacyRuntime.ttf"); // 동적 폰트 → OS 폴백으로 한글 표시 가능
        EnsureNetworkManager();
        EnsureEventSystem();
        BuildUI();
        SetConnectedUI(false);
        _createBtn.interactable = false;
        _joinBtn.interactable = false;
    }

    async void Start()
    {
        try
        {
            SetStatus("Unity Services 초기화 중...");
            var init = new InitializationOptions();
            // 같은 PC에서 인스턴스를 여러 개 띄워도 서로 다른 익명 계정이 되도록 프로필을 분리한다.
            init.SetProfile("nettest" + UnityEngine.Random.Range(0, 1_000_000));
            await UnityServices.InitializeAsync(init);
            if (!AuthenticationService.Instance.IsSignedIn)
                await AuthenticationService.Instance.SignInAnonymouslyAsync();

            SetStatus($"준비 완료 (PlayerId {AuthenticationService.Instance.PlayerId}). [방 만들기] 또는 코드 입력 후 [참가].");
            _createBtn.interactable = true;
            _joinBtn.interactable = true;
            HandleCommandLine();
        }
        catch (Exception e)
        {
            SetStatus("초기화 실패: " + e.Message +
                      "\nEdit > Project Settings > Services 에서 프로젝트 연결, 대시보드에서 Lobby/Relay 활성화를 확인하세요.");
            Debug.LogException(e);
        }
    }

    void OnDestroy()
    {
        UnhookNetcode();
    }

    // ------------------------------------------------------------------ 자동 테스트 (커맨드라인)
    // 빌드된 플레이어를 인자로 구동해 사람 손 없이 검증할 수 있게 한다.
    //   NetTest.exe -host                       : 방을 만든다 (Player 1)
    //   NetTest.exe -join ABC123 -text 안녕      : 코드로 참가(Player 2)하고 연결되면 텍스트를 1회 보낸다
    //   -batchmode -nographics 와 함께 쓰면 창 없이 동작한다.

    void HandleCommandLine()
    {
        var args = Environment.GetCommandLineArgs();
        string joinCode = GetArg(args, "-join");
        string autoText = GetArg(args, "-text");
        bool host = Array.IndexOf(args, "-host") >= 0;

        if (host)
        {
            OnCreateClicked();
        }
        else if (!string.IsNullOrEmpty(joinCode))
        {
            _joinInput.text = joinCode;
            OnJoinClicked();
        }
        if (!string.IsNullOrEmpty(autoText) && (host || !string.IsNullOrEmpty(joinCode)))
            StartCoroutine(AutoSendWhenConnected(autoText));
    }

    static string GetArg(string[] args, string key)
    {
        for (int i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], key, StringComparison.OrdinalIgnoreCase)) return args[i + 1];
        return null;
    }

    IEnumerator AutoSendWhenConnected(string text)
    {
        float t = 0f;
        while (!_connected && t < 60f) { t += Time.deltaTime; yield return null; }
        if (!_connected) { SetStatus("자동 전송 실패: 60초 안에 연결되지 않음"); yield break; }
        // 상대(호스트)가 우리 접속을 처리할 시간을 잠깐 준다
        yield return new WaitForSeconds(1f);
        _chatInput.text = text;
        OnSendClicked();
    }

    // ------------------------------------------------------------------ session

    async void OnCreateClicked()
    {
        if (_busy) return;
        try
        {
            SetBusy(true);
            SetStatus("세션 생성 중 (Lobby + Relay 할당)...");
            var options = new SessionOptions { MaxPlayers = MaxPlayers, Name = "NetTest" }.WithRelayNetwork();
            _session = await MultiplayerService.Instance.CreateSessionAsync(options);
            _myIndex = 1;
            AfterJoined();
        }
        catch (Exception e)
        {
            SetStatus("방 생성 실패: " + e.Message);
            Debug.LogException(e);
            SetBusy(false);
        }
    }

    async void OnJoinClicked()
    {
        if (_busy) return;
        var code = _joinInput.text.Trim().ToUpperInvariant();
        if (code.Length == 0) { SetStatus("방 코드를 입력하세요."); return; }
        try
        {
            SetBusy(true);
            SetStatus($"세션 참가 중 ({code})...");
            _session = await MultiplayerService.Instance.JoinSessionByCodeAsync(code);
            _myIndex = 2;
            AfterJoined();
        }
        catch (Exception e)
        {
            SetStatus("참가 실패: " + e.Message);
            Debug.LogException(e);
            SetBusy(false);
        }
    }

    async void OnLeaveClicked()
    {
        try { if (_session != null) await _session.LeaveAsync(); }
        catch (Exception e) { Debug.LogException(e); }
        ResetLocal();
        SetStatus("세션을 나갔습니다.");
    }

    void AfterJoined()
    {
        _codeText.text = "방 코드: " + _session.Code;
        _session.PlayerJoined += id => SetStatus($"상대 참가 (인원 {_session.PlayerCount}/{MaxPlayers}) — NGO 연결 대기");
        _session.PlayerLeaving += id => SetStatus("상대가 세션을 떠났습니다.");
        _session.RemovedFromSession += () => { SetStatus("세션에서 제거됨 (호스트가 종료했을 수 있음)"); ResetLocal(); };
        _session.Deleted += () => { SetStatus("세션이 삭제되었습니다."); ResetLocal(); };
        StartCoroutine(WaitForNetcode());
    }

    IEnumerator WaitForNetcode()
    {
        // WithRelayNetwork() 세션은 SDK 가 NetworkManager 의 Host/Client 시작을 대신 해준다. 시작될 때까지 기다린다.
        float t = 0f;
        while ((NetworkManager.Singleton == null || !NetworkManager.Singleton.IsListening) && t < NetcodeStartTimeout)
        {
            t += Time.deltaTime;
            yield return null;
        }

        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening)
        {
            SetStatus($"NGO 시작 실패: NetworkManager 가 {NetcodeStartTimeout:0}초 안에 시작되지 않았습니다. Console 을 확인하세요.");
            SetBusy(false);
            yield break;
        }

        nm.CustomMessagingManager.RegisterNamedMessageHandler(MsgName, OnTextMessage);
        UnhookNetcode();
        _onConnected = id => SetStatus($"NGO 연결됨: clientId {id}. 이제 양쪽에서 텍스트를 보내 보세요.");
        _onDisconnected = id => SetStatus($"NGO 연결 해제: clientId {id}");
        nm.OnClientConnectedCallback += _onConnected;
        nm.OnClientDisconnectCallback += _onDisconnected;

        var role = nm.IsHost ? "Host" : "Client";
        SetStatus($"연결 완료 — 나는 Player {_myIndex} ({role}, clientId {nm.LocalClientId}). 아래에 텍스트를 입력해 [보내기].");
        _leftHeader.text  = _myIndex == 1 ? "Player 1 (Host) — 나" : "Player 1 (Host)";
        _rightHeader.text = _myIndex == 2 ? "Player 2 (Client) — 나" : "Player 2 (Client)";
        SetConnectedUI(true);
        SetBusy(false);
        _connected = true;
    }

    void ResetLocal()
    {
        _connected = false;
        _session = null;
        UnhookNetcode();
        var nm = NetworkManager.Singleton;
        if (nm != null && nm.IsListening) nm.Shutdown();   // SDK 가 이미 내렸으면 no-op
        _codeText.text = "";
        _leftText.text = "";
        _rightText.text = "";
        _leftHeader.text = "Player 1 (Host)";
        _rightHeader.text = "Player 2 (Client)";
        SetConnectedUI(false);
        SetBusy(false);
    }

    void UnhookNetcode()
    {
        var nm = NetworkManager.Singleton;
        if (nm == null) return;
        if (_onConnected != null) nm.OnClientConnectedCallback -= _onConnected;
        if (_onDisconnected != null) nm.OnClientDisconnectCallback -= _onDisconnected;
        _onConnected = null;
        _onDisconnected = null;
    }

    // ------------------------------------------------------------------ messaging (Docs/205 5장과 같은 이름 붙은 메시지 방식)

    void OnSendClicked()
    {
        var text = _chatInput.text;
        if (string.IsNullOrEmpty(text)) return;

        ShowText(_myIndex, text);   // 내 쪽은 즉시 표시

        var nm = NetworkManager.Singleton;
        if (nm == null || !nm.IsListening) { SetStatus("연결되어 있지 않습니다."); return; }

        var writer = new FastBufferWriter(128, Allocator.Temp, 64 * 1024);
        using (writer)
        {
            writer.WriteValueSafe(_myIndex);
            writer.WriteValueSafe(text);

            if (nm.IsServer)
            {
                foreach (var id in nm.ConnectedClientsIds)
                    if (id != nm.LocalClientId)
                        nm.CustomMessagingManager.SendNamedMessage(MsgName, id, writer, NetworkDelivery.ReliableSequenced);
            }
            else
            {
                nm.CustomMessagingManager.SendNamedMessage(MsgName, NetworkManager.ServerClientId, writer, NetworkDelivery.ReliableSequenced);
            }
        }
        SetStatus($"보냄: \"{text}\" ({text.Length}자)");
        _chatInput.text = "";
        _chatInput.ActivateInputField();
    }

    void OnTextMessage(ulong senderClientId, FastBufferReader reader)
    {
        reader.ReadValueSafe(out int idx);
        reader.ReadValueSafe(out string text);
        ShowText(idx, text);
        SetStatus($"받음: Player {idx} (clientId {senderClientId}) → \"{text}\"");
    }

    void ShowText(int playerIndex, string text)
    {
        if (playerIndex == 1) _leftText.text = text;
        else if (playerIndex == 2) _rightText.text = text;
    }

    // ------------------------------------------------------------------ runtime setup

    void EnsureNetworkManager()
    {
        if (NetworkManager.Singleton != null || FindFirstObjectByType<NetworkManager>() != null) return;
        var go = new GameObject("NetworkManager (runtime)");
        var nm = go.AddComponent<NetworkManager>();
        var transport = go.AddComponent<UnityTransport>();
        nm.NetworkConfig = new NetworkConfig
        {
            NetworkTransport = transport,
            EnableSceneManagement = false,   // 씬 동기화 불필요 (Docs/205: 오브젝트·씬 동기화 미사용)
        };
    }

    void EnsureEventSystem()
    {
        if (FindFirstObjectByType<EventSystem>() != null) return;
        var go = new GameObject("EventSystem (runtime)");
        go.AddComponent<EventSystem>();
        go.AddComponent<InputSystemUIInputModule>();   // 신 Input System 전용 모드에서도 동작
    }

    // ------------------------------------------------------------------ UI

    void BuildUI()
    {
        var canvasGo = new GameObject("Canvas (runtime)", typeof(RectTransform));
        var canvas = canvasGo.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        var scaler = canvasGo.AddComponent<CanvasScaler>();
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(1920, 1080);
        scaler.matchWidthOrHeight = 0.5f;
        canvasGo.AddComponent<GraphicRaycaster>();
        var root = canvasGo.transform;

        Panel(root, new Vector2(0, 0), new Vector2(1, 1), new Color(0.12f, 0.12f, 0.14f));

        Label(root, new Vector2(0, 0.92f), new Vector2(1, 1),
              "멀티플레이 검증 씬 — Unity Multiplayer Services (Sessions + Relay) + Netcode for GameObjects",
              34, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);

        _status = Label(root, new Vector2(0.03f, 0.83f), new Vector2(0.97f, 0.92f), "", 24, TextAnchor.MiddleCenter, new Color(1f, 0.9f, 0.5f));

        _createBtn = ButtonUI(root, new Vector2(0.05f, 0.74f), new Vector2(0.22f, 0.82f), "방 만들기", OnCreateClicked);
        _joinInput = Input(root, new Vector2(0.30f, 0.74f), new Vector2(0.55f, 0.82f), "방 코드 6자리");
        _joinInput.characterLimit = 8;
        _joinBtn = ButtonUI(root, new Vector2(0.56f, 0.74f), new Vector2(0.70f, 0.82f), "참가", OnJoinClicked);
        _leaveBtn = ButtonUI(root, new Vector2(0.78f, 0.74f), new Vector2(0.95f, 0.82f), "나가기", OnLeaveClicked);

        _codeText = Label(root, new Vector2(0, 0.65f), new Vector2(1, 0.73f), "", 52, TextAnchor.MiddleCenter, new Color(0.5f, 1f, 0.6f), FontStyle.Bold);

        var left = Panel(root, new Vector2(0.03f, 0.15f), new Vector2(0.485f, 0.64f), new Color(0.18f, 0.22f, 0.32f));
        _leftHeader = Label(left, new Vector2(0, 0.86f), new Vector2(1, 1), "Player 1 (Host)", 30, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        _leftText = Label(left, new Vector2(0.03f, 0.03f), new Vector2(0.97f, 0.84f), "", 48, TextAnchor.MiddleCenter, Color.white);

        var right = Panel(root, new Vector2(0.515f, 0.15f), new Vector2(0.97f, 0.64f), new Color(0.32f, 0.20f, 0.18f));
        _rightHeader = Label(right, new Vector2(0, 0.86f), new Vector2(1, 1), "Player 2 (Client)", 30, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        _rightText = Label(right, new Vector2(0.03f, 0.03f), new Vector2(0.97f, 0.84f), "", 48, TextAnchor.MiddleCenter, Color.white);

        _chatInput = Input(root, new Vector2(0.05f, 0.04f), new Vector2(0.75f, 0.12f), "보낼 텍스트 입력 (Enter 또는 [보내기])");
        _chatInput.characterLimit = 200;
        _chatInput.onSubmit.AddListener(_ => OnSendClicked());
        _sendBtn = ButtonUI(root, new Vector2(0.77f, 0.04f), new Vector2(0.95f, 0.12f), "보내기", OnSendClicked);
    }

    void SetConnectedUI(bool connected)
    {
        _chatInput.interactable = connected;
        _sendBtn.interactable = connected;
        _leaveBtn.interactable = connected;
        _createBtn.gameObject.SetActive(!connected);
        _joinBtn.gameObject.SetActive(!connected);
        _joinInput.gameObject.SetActive(!connected);
    }

    void SetBusy(bool busy)
    {
        _busy = busy;
        if (!_createBtn.gameObject.activeSelf) return;
        _createBtn.interactable = !busy;
        _joinBtn.interactable = !busy;
    }

    void SetStatus(string msg)
    {
        if (_status != null) _status.text = msg;
        Debug.Log("[NetTest] " + msg);
    }

    // ---- 위젯 팩토리 (legacy uGUI — TMP 필수 리소스 없이도 동작)

    static RectTransform Make(string name, Transform parent, Vector2 aMin, Vector2 aMax)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        rt.anchorMin = aMin;
        rt.anchorMax = aMax;
        rt.offsetMin = new Vector2(6, 6);
        rt.offsetMax = new Vector2(-6, -6);
        return rt;
    }

    RectTransform Panel(Transform parent, Vector2 aMin, Vector2 aMax, Color color)
    {
        var rt = Make("Panel", parent, aMin, aMax);
        rt.gameObject.AddComponent<Image>().color = color;
        return rt;
    }

    Text Label(Transform parent, Vector2 aMin, Vector2 aMax, string text, int size, TextAnchor align, Color color, FontStyle style = FontStyle.Normal)
    {
        var rt = Make("Text", parent, aMin, aMax);
        var t = rt.gameObject.AddComponent<Text>();
        t.font = _font;
        t.fontSize = size;
        t.fontStyle = style;
        t.alignment = align;
        t.color = color;
        t.text = text;
        t.horizontalOverflow = HorizontalWrapMode.Wrap;
        t.verticalOverflow = VerticalWrapMode.Overflow;
        t.raycastTarget = false;
        return t;
    }

    Button ButtonUI(Transform parent, Vector2 aMin, Vector2 aMax, string label, UnityEngine.Events.UnityAction onClick)
    {
        var rt = Make("Button " + label, parent, aMin, aMax);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = new Color(0.25f, 0.55f, 0.95f);
        var btn = rt.gameObject.AddComponent<Button>();
        btn.targetGraphic = img;
        var colors = btn.colors;
        colors.disabledColor = new Color(0.4f, 0.4f, 0.4f, 0.6f);
        btn.colors = colors;
        btn.onClick.AddListener(onClick);
        Label(rt, Vector2.zero, Vector2.one, label, 28, TextAnchor.MiddleCenter, Color.white, FontStyle.Bold);
        return btn;
    }

    InputField Input(Transform parent, Vector2 aMin, Vector2 aMax, string placeholder)
    {
        var rt = Make("Input", parent, aMin, aMax);
        var img = rt.gameObject.AddComponent<Image>();
        img.color = new Color(0.95f, 0.95f, 0.95f);
        var field = rt.gameObject.AddComponent<InputField>();
        field.targetGraphic = img;

        var textComp = Label(rt, Vector2.zero, Vector2.one, "", 28, TextAnchor.MiddleLeft, Color.black);
        textComp.supportRichText = false;
        textComp.GetComponent<RectTransform>().offsetMin = new Vector2(16, 6);
        textComp.GetComponent<RectTransform>().offsetMax = new Vector2(-16, -6);

        var ph = Label(rt, Vector2.zero, Vector2.one, placeholder, 28, TextAnchor.MiddleLeft, new Color(0.5f, 0.5f, 0.5f), FontStyle.Italic);
        ph.GetComponent<RectTransform>().offsetMin = new Vector2(16, 6);
        ph.GetComponent<RectTransform>().offsetMax = new Vector2(-16, -6);

        field.textComponent = textComp;
        field.placeholder = ph;
        field.lineType = InputField.LineType.SingleLine;
        return field;
    }
}
