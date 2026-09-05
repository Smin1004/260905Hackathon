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

## 3. 방 시스템

| 항목 | 설계 |
|---|---|
| 생성 | 방장이 `MultiplayerService.Instance.CreateSessionAsync(options)` → `session.Code`(6자리)를 화면에 크게 표시 |
| 참가 | 코드 입력 → `MultiplayerService.Instance.JoinSessionByCodeAsync(code)` |
| 인원 | `SessionOptions.MaxPlayers = 2` (3·4인 확장은 `101_extra_design.md`) |
| 전송 | `options.WithRelayNetwork()` — 세션 생성 시 Relay 할당과 NGO `NetworkManager` 시작을 SDK가 처리 |
| 역할 | 방장 = NGO Host, 참가자 = NGO Client. 메시지는 항상 Host↔Client 1:1 |
| 동기화 | 방 설정은 **세션 프로퍼티**로 저장 — 참가자가 세션에 들어오면 즉시 읽을 수 있음 |

### 세션 프로퍼티 (방 설정 — `100_game_design.md` 7.1과 1:1)

```
ParTimeMode   : "0" | "1"            (기본 "0")
AttemptLimit  : "0" | "3" | "5"      (0=무한)
DrawTimeLimit : "120" | "300" | "600" (초, 기본 "300")
PlayTimeLimit : "120" | "180" | "300" (초, 기본 "180" — 검증·교환 공용)
```

- 세션 프로퍼티 값은 문자열이므로 `RoomSettings` ↔ 문자열 변환은 `NetService` 내부에서 처리. 가시성은 `Member`(세션 참가자만 읽음)
- 참가자는 `session.Properties`를 읽어 `MatchData.Settings`에 채운다 (`201_common.md` 3장)

## 4. 매치 흐름 FSM (Boot 소유, 통합 담당)

```
WaitingRoom → VowSelect → MapEdit → Verify → WaitingSubmit → ExchangePlay → Result
```

| 상태 | 전환 조건 | 씬 |
|---|---|---|
| WaitingRoom | 양쪽 접속 완료 (`session.PlayerCount == 2` 또는 NGO `OnClientConnectedCallback`) | Lobby |
| VowSelect | 양쪽 뜻 확정 동기화 | Lobby |
| MapEdit | 진입 즉시 (그리기 타이머 로컬 카운트) | MapEditor |
| Verify | 자기 검증 결과에 따라 개별 진행 (비동기) | Play(검증 모드) ↔ MapEditor |
| WaitingSubmit | 양쪽 제출(검증 클리어) 완료 | MapEditor 대기 오버레이 |
| ExchangePlay | 양쪽 제출 확인 → 동시 시작 | Play(교환 모드) |
| Result | 양쪽 결과 수신 완료 | Result |

- 상태는 **양쪽 조건이 충족될 때만 전환** — 각자의 진행 완료를 메시지로 보고받아 통과
- Verify는 비동기: 한쪽이 그리는 중에 다른 쪽이 검증 가능
- 타이머(그리기·플레이)는 로컬 카운트, 만료 처리 룰은 `100_game_design.md` 7.3·8장

## 5. 메시지 정의 (NGO 이름 붙은 메시지)

`NetworkManager.CustomMessagingManager.SendNamedMessage(name, clientId, writer, delivery)` 기반. 이름은 아래 표의 메시지명 문자열을 그대로 쓴다.

| 메시지명 | 페이로드 | 방향 | 전달 방식 |
|---|---|---|---|
| `VowSelected` | `vowId`(int), `nickname`(string) | 양방향 | ReliableSequenced |
| `MapChunk` | `chunkIndex`(int), `chunkCount`(int), `bytes`(byte[] ≤ 4KB) — `MapSerializer.Serialize(map)` 결과(양자화 바이너리+GZip)를 `MapChunker.Split`으로 4KB 분할 | 각자 → 상대 | ReliableSequenced |
| `VerifyComplete` | `parTime`(float) | 각자 → 상대 | ReliableSequenced |
| `PlayResult` | `PlayerRecord` (`206_ranking.md`) — 필드 4개 직접 직렬화 | 각자 → 상대 | ReliableSequenced |
| `SubmitFailed` | 없음 — 검증 단계 총 상한 초과 통지 → 수신측 승리로 Result 전환 (`100_game_design.md` 6장) | 각자 → 상대 | ReliableSequenced |
| (콜백) 접속 끊김 | NGO `OnClientDisconnectCallback` / 세션 `RemovedFromSession` → `MatchAbort` 이벤트로 변환 | 시스템 | — |

- 데이터 전송은 **확정 시점에만** (그리기 중 전송 없음 — 라이브 관전은 스코프 아웃)
- **맵 청크 분할이 필수**: NGO/Unity Transport의 기본 최대 페이로드(수 KB)보다 `MapData`가 크다. 송신측은 `MapSerializer.Serialize` → `MapChunker.Split`(4KB)로 나눠 순서대로 보내고, 수신측은 `MapChunkAssembler.Add`로 모아 완성 시 `MapSerializer.Deserialize` → `MatchData.OpponentMap`. `MapChunk`는 ReliableSequenced라 순서·누락 걱정 없음. 송신 지점은 `MapEditorController.Completed(map, payload)` 이벤트
- `MapData` 자체 크기는 양자화·다운샘플로 ≤100KB 유지 (`203_map_editor.md` 5장) → 청크 25개 이내
- 수신측은 `MatchData`에 반영 후 Boot FSM에 C# 이벤트로 보고

## 6. `NetService` 래퍼 싱글턴 (네트워크 담당 납품)

```csharp
class NetService : MonoBehaviour {
    // 초기화 — Boot에서 1회 (UnityServices.InitializeAsync → 익명 로그인)
    Task Init();

    // Lobby 씬용
    Task<string> CreateRoom(RoomSettings settings, string nickname);  // 반환: 참가 코드
    Task JoinRoom(string roomCode, string nickname);
    RoomSettings CurrentSettings { get; }   // 세션 프로퍼티에서 읽은 값
    bool IsHost { get; }

    // 매치용
    void SendVow(VowId id);          // nickname 동봉
    void SendMap(MapData map);       // 내부에서 청크 분할
    void SendVerify(float parTime);
    void SendResult(PlayerRecord record);
    void SendSubmitFailed();

    // 콜백 이벤트 — 상대 입력 수신 시 상위(Boot FSM)에 전파
    event Action OnOpponentJoined;
    event Action<VowId, string> OnVowReceived;
    event Action<MapData> OnMapReceived;          // 청크 조립 완료 시 1회
    event Action<float> OnVerifyReceived;
    event Action<PlayerRecord> OnResultReceived;
    event Action OnSubmitFailedReceived;
    event Action<string> OnMatchAbort;            // 사유 문구
    event Action<string> OnError;                 // 코드 틀림 등 로비 오류
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
| 플레이 중 끊김 (참가자) | Host가 `OnClientDisconnectCallback` 수신 → `OnMatchAbort` → 즉시 결과 화면, 무효 처리 (`100_game_design.md` 8장) |
| 플레이 중 끊김 (방장) | 세션 종료 → 참가자에게 `RemovedFromSession` → 동일하게 `OnMatchAbort`. MVP: 호스트 이관·재접속 미지원 |
| 그리기 시간 만료 | 로컬 판단. 골 없으면 골 배치만 허용 → 배치 즉시 검증 진입 (`100_game_design.md` 5장) |
| 검증 단계 총 상한 초과 | `SubmitFailed` 전파 → 상대 승리로 매치 종료 (`100_game_design.md` 6장) |
| 플레이 시간 만료 | 로컬 판단 → `PlayResult`(GaveUp=true) 전송. 양쪽 모두 반드시 결과를 보내므로 Result 전환 보장 |
| 서비스 초기화 실패 (오프라인 등) | `Init()` 예외 → 로비에 "네트워크 연결 실패" 1문장, 재시도 버튼 |

## 8. 구현 현황 (2026-09-05)

| 파일 | 역할 |
|---|---|
| `Scripts/Network/NetService.cs` | 6장 래퍼 구현. `Init`(익명 로그인, 인스턴스별 프로필) → `CreateRoom`/`JoinRoom`(Sessions + Relay, SDK 가 NGO Host/Client 자동 시작) → NGO 시작 대기 후 이름 붙은 메시지 핸들러 등록. 메시지: `CJ_Hello`(닉네임 + 호스트→클라 방 설정 6종: 패타임 모드·시도·그리기·플레이 시간·뜻 개수·후보 수), `CJ_Vows`(뜻 ID 목록 — 양쪽 확정 시 교환), `CJ_MapMeta`(패타임·크기·청크 수), `CJ_MapChunk`(4KB, ReliableFragmentedSequenced), `CJ_PlayResult`, `CJ_NextRound`, `CJ_SubmitFailed`(그리기 시간 초과 → 그 라운드 패배). 끊김은 NGO 연결 해제 콜백 + 세션 이벤트 → `MatchAborted` 1회 |
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
- 미구현: 뜻 선택 단계(VowSelect — 뜻 시스템 자체가 미구현), 그리기 시간 제한, 방 설정 UI(기본값 고정), 검증 단계 총 상한 `SubmitFailed`, 재접속

## 9. 테스트 체크리스트

- [x] 팀 대표 커밋 후 다른 팀원 PC에서 추가 설정 없이 `Init()` 성공 (프로젝트 연결 공유 확인) — 빌드 클라이언트에서 확인
- [x] 방 생성 → 코드 표시 → 코드 참가 → 2인 접속 확인 (에디터 호스트 + 개발 빌드 클라이언트)
- [ ] 세션 프로퍼티 → 참가자 `RoomSettings` 4종 값 일치
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
