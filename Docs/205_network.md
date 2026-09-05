# 205 — 네트워크 구현 (Unity Multiplayer Services + Netcode for GameObjects)

> 방 시스템과 매치 흐름 동기화. 소유: 네트워크 담당 (`NetService` 래퍼 싱글턴) + 통합 담당 (Boot FSM 배선).
> 설계 전제: **실시간 물리 동기화 불필요** — 각자 로컬 플레이, 상태·데이터·결과만 동기화.
>
> **결정 (2026-09-05)**: 백엔드는 **Unity Gaming Services의 Multiplayer Services SDK(Sessions) + Relay + Netcode for GameObjects(NGO)**. PUN2는 폐기. 비교 근거는 부록 A.

## 1. 선택 근거

- **방 코드 UX가 내장**: `CreateSessionAsync`가 세션을 만들면 6자리 참가 코드(`session.Code`)가 바로 나온다. 갈틱폰식 "코드만 공유하면 접속"과 일치
- **엔진 안에서 완결**: 외부 사이트 가입 없음. Unity 계정으로 에디터의 Project Settings → Services에서 프로젝트를 연결하면 끝. 런타임은 익명 로그인
- **이미 준비된 환경**: 현재 프로젝트에 Multiplayer Center 패키지가 설치되어 있어 필요한 패키지 설치를 안내받을 수 있음
- **비용**: Relay 무료 한도 월평균 50 CCU — 2인 매치 시연에 충분
- 턴 기반 비동기 흐름 → NGO의 이름 붙은 메시지(`CustomMessagingManager`) 수준의 경량 동기화로 충분. NetworkVariable·NetworkTransform 등 실시간 동기화 기능은 **사용하지 않는다**

## 2. 준비 (개발 시작 전)

### 2.1 팀 대표 1명이 한 번만

| 순서 | 작업 |
|---|---|
| 1 | Edit → Project Settings → Services → Unity Cloud 프로젝트 **생성 또는 연결** (Unity 계정 필요) |
| 2 | Package Manager에서 `com.unity.services.multiplayer`(Multiplayer Services), `com.unity.netcode.gameobjects`(NGO), `com.unity.transport` 설치 — Multiplayer Center 창의 권장 구성 버튼으로 일괄 설치 가능 |
| 3 | Unity Cloud 대시보드에서 **Relay**와 **Lobby** 서비스 활성화 (프로젝트 연결 시 자동 활성화되는 경우도 있음 — 확인만) |
| 4 | `Packages/manifest.json`, `ProjectSettings/ProjectSettings.asset`(cloudProjectId 포함) 커밋 → 팀 전원에게 공유됨 |

- 연결 정보가 `ProjectSettings/`에 들어가므로 **다른 팀원은 별도 설정 없이** 풀만 받으면 같은 프로젝트로 붙는다
- `ProjectSettings/` 커밋 규칙(`201_common.md` 7장)에 따라 통합 담당이 커밋

### 2.2 테스트 환경

- 한 PC에서 2인 테스트: Unity 6의 **Multiplayer Play Mode** 패키지(`com.unity.multiplayer.playmode`)로 에디터 안에 가상 플레이어를 띄운다. 별도 빌드나 프로젝트 복제 없이 방 생성/참가를 반복 테스트할 수 있음
- 최종 시연: 서로 다른 PC 2대, 다른 네트워크에서도 코드 참가가 되는지 확인 (Relay 경유이므로 공유기 설정 불필요)

### 2.3 WebGL 빌드 (2026-09-06 설정)

- 메뉴 **[Chojiilgwan > WebGL > Build]** → `Builds/WebGL/` (gitignore). 설정만 적용하려면 [Chojiilgwan > WebGL > Apply Settings] (`Scripts/Common/WebBuild.cs`)
- 플레이어 설정: 압축 **Gzip + 압축 해제 폴백**(서버 헤더 없이 정적 호스팅 어디서나 실행), 코드 스트리핑 **Low**(Services·Netcode 리플렉션 보호), IL2CPP Release·OptimizeSize, 예외 지원 ExplicitlyThrownExceptionsOnly, 기본 캔버스 1280×720, runInBackground
- 네트워크: 브라우저는 UDP 를 못 쓰므로 Relay **WSS**. Multiplayer Services SDK 가 WebGL 에서 자동으로 wss 할당을 고르고(`RelayProtocol.Default = WSS`), UnityTransport 는 `RelayServerData.IsWebSocket` 을 보고 웹소켓으로 붙는다. `NetService.EnsureNetworkManager` 가 `UNITY_WEBGL` 이 정의될 때 `UseWebSockets = true` 를 켠다 — **에디터에서 빌드 타깃이 WebGL 이면 에디터 플레이도 wss** 로 붙는다(SDK 가 같은 심볼로 wss 할당을 고르므로 안 켜면 "Mismatched Relay configuration" 으로 방 생성 실패). 에디터 콘솔의 "Could not do Qos region selection" 은 WebGL 에서 QoS 미지원 경고일 뿐 정상 동작. 데스크톱 빌드·에디터(DTLS)와 WebGL(WSS)이 **같은 방에 섞여도 됨** — Relay 가 중계
- 실행: `file://` 로는 열리지 않는다. 로컬 확인은 `Builds/WebGL` 에서 `python -m http.server 8000` 뒤 `http://localhost:8000`. 배포는 itch.io(HTML 프로젝트, 압축 zip 업로드) 또는 GitHub Pages
- WebGL 차이점: 명령행 인자가 없어 `AutoPilot` 미동작(자동화는 데스크톱 빌드로), 클립보드는 `Plugins/WebGL/Clipboard.jslib`(`Scripts/Common/Clipboard.cs`) 경유, AudioClip Streaming 미지원(SoundBank 는 CompressedInMemory), 첫 로딩 수십 초(약 30~40MB 다운로드), 브라우저 탭이 비활성이면 프레임이 느려질 수 있음(runInBackground 로 타이머는 유지)
- ⚠ 확인 필요: 실기기 브라우저 2대(또는 브라우저 + 데스크톱 빌드)로 방 생성·참가·맵 교환까지 한 번 통과시킬 것. Relay WSS 는 방화벽/프록시 환경에서 443 포트를 쓰므로 회사 네트워크에서도 대체로 열려 있다

## 3. 방 시스템

| 항목 | 설계 |
|---|---|
| 생성 | 방장이 `MultiplayerService.Instance.CreateSessionAsync(options)` → `session.Code`(6자리)를 화면에 크게 표시 |
| 참가 | 코드 입력 → `MultiplayerService.Instance.JoinSessionByCodeAsync(code)` |
| 인원 | `SessionOptions.MaxPlayers = 2` (3·4인 확장은 `101_extra_design.md`) |
| 전송 | `options.WithRelayNetwork()` — 세션 생성 시 Relay 할당과 NGO `NetworkManager` 시작을 SDK가 처리 |
| 역할 | 방장 = NGO Host, 참가자 = NGO Client. 메시지는 항상 Host↔Client 1:1 |
| 동기화 | 방 설정은 **세션 프로퍼티**로 저장 — 참가자가 세션에 들어오면 즉시 읽을 수 있음 |

### 방 설정 5종 (`100_game_design.md` 7.1과 1:1 — `RoomSettings`)

```
AttemptLimit      : int   0 | 3 | 5        (0=무한)
DrawTimeLimit     : int   120 | 300 | 600  (초, 기본 300)
PlayTimeLimit     : int   120 | 180 | 300  (초, 기본 180 — 검증·교환 공용)
VowPickCount      : int   (기본 1 — 라운드마다 고르는 뜻 개수)
VowCandidateCount : int   (기본 5 — 뜻 후보 수, 0=전체)
```

- **구현은 세션 프로퍼티 대신 `CJ_Hello` 메시지**로 호스트가 참가자에게 전달한다 (5장, 8장). 참가자는 Hello 를 받으면 `MatchData.Settings`에 채운다 (`201_common.md` 3장). 세션 프로퍼티 방식은 설계 참고로만 남긴다

## 4. 매치 흐름 FSM (Boot 소유, 통합 담당 — `GameFlow.MatchState`)

```
Lobby → RoomLobby → VowSelect → MapEdit → WaitingSubmit → ExchangePlay → WaitingResult → Result → WaitingNextRound ─┐
                              ↑                                                                                          │
                              └──────────────────────── (Round+1, 같은 방) ───────────────────────────────────────────────┘
어느 상태에서든 → Aborted (연결 끊김 / 방 나가기)
```

| 상태 | 진입·전환 조건 | 화면 |
|---|---|---|
| Lobby | 초기 상태·복귀 상태. [방 만들기] / 코드 [참가] | Boot 로비 패널 |
| RoomLobby | 방 생성·참가 직후의 **방 화면**. Hello 교환(`OpponentReady`)이 끝나면 방장의 [게임 시작] 이 활성화. 방장 `CJ_Start` 송신 ∧ 참가자 수신 → 양쪽 VowSelect. 방장이 설정을 바꾸면 `CJ_Settings` | Boot 방 패널 (코드·플레이어·설정) |
| VowSelect | 후보 제시. **내 뜻 확정(`CJ_Vows` 송신) ∧ 상대 뜻 수신** → MapEdit | Boot 뜻 선택 패널 |
| MapEdit | MapEditor 애디티브 로드, 그리기 타이머 시작(로컬). 검증 플레이는 이 상태 안에서 진행(씬 전환 없음). 내 [완료] → 에디터 잠금 + 맵 전송 → WaitingSubmit. **타이머 만료(미제출)** → `CJ_SubmitFailed` 송신 → Result(패배) | MapEditor + 방 나가기 띠 + 상대 뜻 패널 |
| WaitingSubmit | **내 제출 ∧ 상대 맵 수신** → ExchangePlay (각자 독립 판정, 별도 동기화 메시지 없음). 상대 `CJ_SubmitFailed` 수신 → Result(승리) | MapEditor 위 대기 오버레이 |
| ExchangePlay | MapEditor 언로드 → Play 로드. 내 플레이 종료(클리어·기권·시간 만료·시도 소진) → `CJ_PlayResult` 송신 → WaitingResult | Play |
| WaitingResult | **내 결과 전송 ∧ 상대 결과 수신** → Result | Play 위 대기 오버레이 |
| Result | Play 언로드, `Ranking.Judge` 판정 표시. [다음 라운드] → WaitingNextRound. 제출 실패 결과도 이 상태 | Boot 결과 패널 |
| WaitingNextRound | **내 `CJ_NextRound` 송신 ∧ 상대 수신** → Round+1, 라운드 상태 초기화 후 VowSelect (세션·상대·방 설정 유지) | Boot 결과 패널 ([다음 라운드] 비활성 "상대 대기 중...") |
| Aborted | `MatchAborted`(끊김) 또는 [방 나가기]. 콘텐츠 씬 언로드. 끊김이면 **매치 무효 화면**: 호스트는 [같은 방에서 새 상대 기다리기] → RoomLobby(Round 1), 그 외 [방 나가기] → Lobby | Boot 결과 패널 |

- 양쪽 조건이 필요한 전환(VowSelect·WaitingSubmit·WaitingResult·WaitingNextRound)은 **각 클라이언트가 독립적으로 "내 완료 ∧ 상대 메시지 수신"을 판정**한다 — 별도 동기화 메시지 없음
- 검증은 비동기: 한쪽이 그리는 중에 다른 쪽이 검증·제출 가능
- 타이머(그리기·플레이)는 로컬 카운트, 만료 처리 룰은 `100_game_design.md` 6장·7.3·8장

## 5. 메시지 정의 (NGO 이름 붙은 메시지)

`NetworkManager.CustomMessagingManager.SendNamedMessage(name, clientId, writer, delivery)` 기반. 이름은 아래 표의 메시지명 문자열을 그대로 쓴다.

| 메시지명 | 페이로드 (쓰는 순서) | 방향 | 전달 방식 |
|---|---|---|---|
| `CJ_Hello` | `nickname`(string, ≤16자), `isHost`(bool), **호스트만** 방 설정 5종 `AttemptLimit`(int)·`DrawTimeLimit`(int)·`PlayTimeLimit`(int)·`VowPickCount`(int)·`VowCandidateCount`(int), `ack`(bool — 상대 Hello 를 이미 받았음) | 양방향 — 상대 Hello 를 받을 때까지 1초마다 재전송 (8장) | ReliableSequenced |
| `CJ_Vows` | `count`(int), `vowId`(int) × count | 양방향 — 각자 뜻 확정 시 | ReliableSequenced |
| `CJ_Settings` | 방 설정 5종 (int × 5, Hello 와 같은 순서) — 방 화면에서 방장이 바꿀 때마다 | 호스트 → 참가자 | ReliableSequenced |
| `CJ_Start` | 방 설정 5종 (최종 스냅샷) — [게임 시작] | 호스트 → 참가자 | ReliableSequenced |
| `CJ_MapMeta` | `parTime`(float), `totalBytes`(int), `chunkCount`(int) — 맵 전송 시작 (패타임은 여기에 실려 별도 메시지 없음) | 각자 → 상대 | ReliableSequenced |
| `CJ_MapChunk` | `index`(int), `count`(int), `length`(int), `bytes`(≤4KB) — `MapSerializer.Serialize(map)` 결과(양자화 바이너리+GZip)를 `MapChunker.Split`으로 4KB 분할 | 각자 → 상대 | **ReliableFragmentedSequenced** |
| `CJ_PlayResult` | `cleared`(bool), `clearTime`(float), `attemptsUsed`(int), `gaveUp`(bool) — `PlayerRecord` (`206_ranking.md`) | 각자 → 상대 | ReliableSequenced |
| `CJ_NextRound` | 더미 int 1 — [다음 라운드] 준비 완료 | 각자 → 상대 | ReliableSequenced |
| `CJ_SubmitFailed` | 더미 int 1 — 그리기 시간 만료 미제출 통지 → 수신측 승리(양쪽이면 무승부)로 Result 전환 (`100_game_design.md` 6장) | 각자 → 상대 | ReliableSequenced |
| (콜백) 접속 끊김 | NGO `OnClientDisconnectCallback`·`OnTransportFailure`·`OnClientStopped`/`OnServerStopped` / 세션 `PlayerLeaving`·`RemovedFromSession`·`Deleted` → `MatchAborted(사유)` 이벤트 1회 | 시스템 | — |

- 데이터 전송은 **확정 시점에만** (그리기 중 전송 없음 — 라이브 관전은 스코프 아웃)
- **맵 청크 분할이 필수**: NGO/Unity Transport의 기본 최대 페이로드(수 KB)보다 `MapData`가 크다. 송신측은 `MapSerializer.Serialize` → `MapChunker.Split`(4KB)로 나눠 `CJ_MapMeta` 뒤에 순서대로 보내고, 수신측은 `MapChunkAssembler.Add`로 모아 완성 시 `MapSerializer.Deserialize` → `MatchData.OpponentMap`. 순서 보장 전달이라 순서·누락 걱정 없음. 송신 지점은 `MapEditorController.Completed(map, payload)` 이벤트를 `GameFlow`가 받아 `NetService.SendMap(payload, parTime)` 호출
- `MapData` 자체 크기는 양자화·다운샘플로 ≤100KB 유지 (`203_map_editor.md` 5장) → 청크 25개 이내
- 수신측은 `MatchData`에 반영 후 Boot FSM에 C# 이벤트로 보고

## 6. `NetService` 래퍼 싱글턴 (네트워크 담당 납품)

```csharp
class NetService : MonoBehaviour {                      // 구현: Scripts/Network/NetService.cs
    // 초기화 — Boot에서 1회 (UnityServices.InitializeAsync → 익명 로그인, 인스턴스별 프로필)
    Task Init();

    // 방
    Task<string> CreateRoom(RoomSettings settings, string nickname);  // 반환: 참가 코드
    Task JoinRoom(string roomCode, string nickname);                   // 실패 시 예외 (코드 틀림 등)
    Task Leave();                       // 타임아웃 포함 세션 종료
    bool PrepareForNewOpponent();       // 호스트 전용 — 같은 방에서 새 상대 대기
    void ResetForNextRound();           // 라운드 진행 플래그만 초기화
    RoomSettings Settings { get; }      // 호스트 값 (Hello 로 수신)
    bool IsHost, InSession, IsNetcodeUp, IsOpponentReady;  string RoomCode, OpponentNickname, Status;

    // 매치 메시지 (5장)
    void SendVows(IList<VowId> vows);
    void SendMap(byte[] payload, float parTime);   // 내부에서 MapMeta + 청크 분할
    void SendResult(PlayerRecord record);
    void SendNextRound();
    void SendSubmitFailed();

    // 콜백 이벤트 — Boot FSM(GameFlow)이 구독
    event Action<string> StatusChanged;
    event Action<string, RoomSettings> OpponentReady;   // Hello 교환 완료 (상대 닉네임, 방 설정)
    event Action<List<VowId>> VowsReceived;
    event Action<int, int> MapChunkProgress;
    event Action<MapData, float> MapReceived;           // 청크 조립 완료 시 1회 (맵, 패타임)
    event Action<PlayerRecord> ResultReceived;
    event Action NextRoundReceived;
    event Action SubmitFailedReceived;
    event Action<string> MatchAborted;                  // 사유 문구, 매치당 1회
}
```

- 다른 개발자는 이 API만 사용 — `Unity.Services.*`, `Unity.Netcode.*` 타입이 **씬 코드로 새어나가지 않게** 캡슐화. 백엔드를 다시 바꿔도 이 파일 내부만 수정
- Boot FSM이 이벤트 구독 → 상태 전환 결정 (통합 담당 배선)
- 씬에는 `NetworkManager` 프리팹 1개만 존재(Boot 씬, `DontDestroyOnLoad`). NetworkObject를 씬에 배치하지 않는다 — 게임 오브젝트 동기화를 쓰지 않으므로 필요 없음

## 7. 예외 처리

| 상황 | 처리 |
|---|---|
| 방 코드 틀림/존재 없음 | `JoinSessionByCodeAsync` 예외 → `OnError` → 로비에 오류 문구 (재입력 유도) |
| 참가자 3명 이상 시도 | `MaxPlayers = 2`로 자동 거부 |
| 플레이 중 끊김 (참가자) | Host가 `OnClientDisconnectCallback` 수신 → `MatchAborted` → 매치 무효 화면. 호스트는 [같은 방에서 새 상대 기다리기]로 같은 방 코드 유지 가능 (`100_game_design.md` 8장) |
| 플레이 중 끊김 (방장) | 세션 종료 → 참가자에게 `RemovedFromSession` → 동일하게 `MatchAborted` → 매치 무효 화면, [방 나가기]만. MVP: 호스트 이관·재접속 미지원 |
| Hello 전 참가 실패·이탈 | 호스트는 방을 유지하고 다시 기다린다 (Abort 아님) |
| 그리기 시간 만료 (미제출) | 로컬 판단 → 에디터 잠금 + `CJ_SubmitFailed` 전송 → 양쪽 Result(송신측 패배·수신측 승리, 양쪽 동시면 무승부) (`100_game_design.md` 6장) |
| 플레이 시간 만료·기권·시도 소진 (교환) | 로컬 판단 → `CJ_PlayResult`(GaveUp=true) 전송. 양쪽 모두 반드시 결과를 보내므로 Result 전환 보장. 검증 플레이의 시간 만료는 로컬에서 에디터 복귀만 (메시지 없음) |
| 서비스 초기화 실패 (오프라인 등) | `Init()` 예외 → 로비에 "네트워크 연결 실패" 1문장, 재시도 버튼 |

## 8. 구현 현황 (2026-09-05)

| 파일 | 역할 |
|---|---|
| `Scripts/Network/NetService.cs` | 6장 래퍼 구현. `Init`(익명 로그인, 인스턴스별 프로필) → `CreateRoom`/`JoinRoom`(Sessions + Relay, SDK 가 NGO Host/Client 자동 시작) → NGO 시작 대기 후 이름 붙은 메시지 핸들러 등록. 메시지: `CJ_Hello`(닉네임 + 호스트→클라 방 설정 5종: 패타임 모드·시도·그리기·플레이 시간·뜻 개수·후보 수), `CJ_Vows`(뜻 ID 목록 — 양쪽 확정 시 교환), `CJ_MapMeta`(패타임·크기·청크 수), `CJ_MapChunk`(4KB, ReliableFragmentedSequenced), `CJ_PlayResult`, `CJ_NextRound`, `CJ_SubmitFailed`(그리기 시간 초과 → 그 라운드 패배). 끊김은 NGO 연결 해제 콜백 + 세션 이벤트 → `MatchAborted` 1회 |
| `Scripts/Boot/GameFlow.cs` | 4장 FSM. Boot 씬 상주(DontDestroyOnLoad). 로비 UI(닉네임·방 만들기·코드 참가) → Hello 교환 시 MapEditor 애디티브 로드 → 에디터 `Completed` 구독 → 잠금 + `SendMap` → 내 제출 ∧ 상대 맵 수신 시 MapEditor 언로드·Play 로드 → `PlayBootstrap.Finished` 로 결과 전송 → 양쪽 결과 시 `Ranking` 판정 결과 화면. **결과 화면 [다음 라운드]**: 양쪽이 누르면(`CJ_NextRound`) 같은 방에서 MapEdit 부터 반복 — 방을 새로 만들지 않는다. **[방 나가기]**는 에디터 우상단 바·로비 대기·결과 화면에 있으며 제자리 초기화로 로비 복귀(씬 재로드·싱글턴 파괴 없음). 끊김 → "매치 무효" 화면, 호스트는 [같은 방에서 새 상대 기다리기] 가능 |
| `Scripts/Play/PlayBootstrap.cs` | Play 씬 진입점. `MatchData.OpponentMap` 으로 `PlaySession`(교환 모드: 시간·시도 제한, ESC=기권) 실행. 상대 맵이 없으면 데모 맵 (단독 실행 가능) |
| `Scripts/Common/Ranking.cs` | 206 계산식 순수 함수 (`EffectiveTime`, `Score`, `Judge`) |
| `Scripts/Debug/AutoPilot.cs` | 개발 빌드 전용 자동 파일럿. `-autohost` / `-autojoin CODE` 인자로 방 생성·참가, 자동 맵 제작·확정, 교환 플레이 자동 클리어까지 사람 손 없이 진행 → 2 프로세스 종단 테스트용 |

- **방 나가기(Leave)는 타임아웃 포함**: 상대가 먼저 나가 NGO 가 이미 멈춘 상태에서는 SDK `LeaveAsync` 가 정지 콜백을 기다리며 끝나지 않는 경우가 있다(로비 복귀가 간헐적으로 멈추던 원인). `NetService.Leave` 는 4초 타임아웃 후 로컬 정리만 진행하고, `GameFlow` 는 8초 상한으로 기다린 뒤 제자리 초기화한다
- **Hello 핸드셰이크는 재전송 방식**: NGO 는 핸들러가 등록되기 전에 도착한 이름 붙은 메시지를 버린다. 클라이언트는 SDK 참가 완료 후에야 핸들러를 등록하므로, 접속 직후 호스트가 보낸 Hello 가 유실될 수 있다 (실제 테스트에서 재현). 양쪽은 상대의 Hello 를 받을 때까지 1초마다 Hello 를 다시 보내고, 받은 쪽은 상대가 아직 내 Hello 를 못 받았으면(ack=false) 한 번 답장한다. 30초 안에 교신이 없으면 매치 중단
- 방 설정은 세션 프로퍼티 대신 **Hello 메시지**로 호스트가 전달한다 (SDK 프로퍼티 API 의존을 줄이기 위해). 3장의 세션 프로퍼티 표는 설계 참고로 남긴다
- 이름 붙은 메시지는 비조각 전달에서 1264바이트 상한이 있으므로 맵 청크는 반드시 `ReliableFragmentedSequenced` (패키지 소스 확인)
- 양쪽 제출 완료 판정은 각 클라이언트가 독립적으로 "내 제출 ∧ 상대 맵 수신" 을 계산한다 — 별도 동기화 메시지 없음. 결과 화면도 "내 결과 전송 ∧ 상대 결과 수신" 으로 각자 판단
- 런타임 생성 UI 는 소유 오브젝트의 씬으로 옮겨(`RuntimeUI.Canvas(owner)`) 애디티브 씬 언로드 시 함께 정리된다
- 미구현: 재접속·호스트 이관. 뜻 선택(VowSelect)·그리기 시간 만료(`CJ_SubmitFailed`)·라운드 반복(`CJ_NextRound`)은 구현됨 (2026-09-06)

## 9. 테스트 체크리스트

- [x] 팀 대표 커밋 후 다른 팀원 PC에서 추가 설정 없이 `Init()` 성공 (프로젝트 연결 공유 확인) — 빌드 클라이언트에서 확인
- [x] 방 생성 → 코드 표시 → 코드 참가 → 2인 접속 확인 (에디터 호스트 + 개발 빌드 클라이언트)
- [ ] Hello 로 전달된 참가자 `RoomSettings` 5종 값이 호스트와 일치
- [ ] `MapData` 청크 분할 전송 왕복 무손실 (60스트로크 × 300점 상한 맵으로)
- [x] 상태 전환 전부 양방향 동기화 되는지 (한쪽만 빠른 경우 처리) — 자동 파일럿 2 프로세스로 Lobby→Result 전 구간 통과
- [x] 참가자 강제 종료 / 방장 강제 종료 각각 → 상대 화면이 결과(무효)로 전환 — 양방향 확인. 호스트는 같은 방에서 새 상대 수용 확인
- [x] 같은 방 라운드 반복 (2라운드 자동 테스트, 라운드마다 다른 맵 교환)
- [x] 방 나가기 → 로비 복귀 (상대가 먼저 나간 뒤에도 타임아웃으로 복귀)
- [ ] 다른 네트워크의 PC 2대에서 코드 참가 성공 (시연 리허설)

---

## 부록 A. 백엔드 후보 비교 (2026-09-05 리서치 — B로 결정)

요구사항: ① 방 코드만 공유하면 접속 ② 실시간 물리 동기화 불필요, 확정 시점 데이터(≤100KB 맵 JSON) 교환 ③ 외부 서비스 가입·키 발급 최소화 ④ 15시간 안에 배선 가능.

| 후보 | 방 코드 | 외부 가입 | 페이로드 100KB | 구현 난도 | 비고 |
|---|---|---|---|---|---|
| A. Photon PUN2 | 룸 이름 = 코드, 내장 | Photon 대시보드 계정 + AppID | 조각화 가능하나 청크 분할 권장 | 낮음 | 유지보수 모드, Unity 6 지원, 무료 20 CCU. **외부 가입이 걸려 폐기** |
| **B. Unity Multiplayer Services (Sessions + Relay + NGO)** | `session.Code` 내장 | Unity 계정만 (에디터 안에서 프로젝트 연결) | NGO 메시지, 4KB 청크 분할 | 낮음~중간 | **채택**. 엔진 안에서 완결, Multiplayer Center 이미 설치, Relay 무료 50 CCU |
| C. Supabase / Firebase DB | 테이블 행 키 | 서비스 계정 1개 | jsonb 저장, 여유 | 중간 (Unity SDK 수작업, WebSocket 이슈. REST 폴링은 SDK 불필요) | **폴백**. B가 막히면 UnityWebRequest 2초 폴링으로 대체 |
| D. 자체 경량 서버 + 터널 | 자유 | 없음 | 자유 | 높음 | 인력 1명 통째로 소모 |
| E. LAN 직결 (IP 입력) | 코드 대신 IP | 없음 | 자유 | 낮음 | 같은 Wi-Fi 한정 — 시연장 최후 폴백 |

---
**관련 문서**: `201_common.md` · `202_gameplay.md` · `203_map_editor.md` · `206_ranking.md` · `100_game_design.md`
