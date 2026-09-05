# 201 — 공통 기술 규약 (씬 분할 · 데이터 계약 · 협업 규칙)

> 4명이 Unity에서 동시 작업하기 위한 규약. **개발 시작 전 전원이 읽고 동의할 것.**
> 기획 근거: `100_game_design.md`. 충돌 방지가 이 문서의 최대 목적.

## 1. 프로젝트 설정

| 항목 | 값 |
|---|---|
| Unity 버전 | 6000.0.66f2 (Unity 6 — 팀 전원 동일 버전 고정) |
| 입력 | 신 Input System. Active Input Handling이 "Input System Package"든 "Both"든 동작해야 함 (`202_gameplay.md` 1장) |
| 템플릿 | 2D URP |
| 해상도 기준 | 1920×1080, Canvas Scaler = Scale With Screen Size |
| 네트워크 | Unity Multiplayer Services (Sessions + Relay) + Netcode for GameObjects. 씬 코드는 `NetService` API에만 의존 (`205_network.md`) |
| 형상 관리 | Git + LFS. 유니티 YAML 병합 대비: `Edit > Project Settings > Asset Serialization = Force Text` 확인 |

## 2. 씬 분할 & 소유권 (1인 1씬 원칙)

| 씬 | 소유 역할 | 내용 |
|---|---|---|
| **Boot** | UI/통합 담당 | 루트 씬. `GameManager`, 매치 흐름 FSM, `MatchData` 싱글턴, **애디티브 씬 전환** 담당 |
| **Lobby** | 네트워크 담당 | 방 생성/코드 참가, 방 설정 UI, 뜻 선택 UI, 준비 동기화 |
| **MapEditor** | 에디터 담당 | 드로잉 캔버스, 툴 UI, 스트로크 직렬화, 시작점 표시·골 배치. (씬·폴더 이름을 `Editor`로 하면 Unity가 에디터 전용으로 취급해 빌드에서 빠지므로 **MapEditor** 사용) |
| **Play** | 게임플레이 담당 | 플랫포머 물리, 뜻 제약, 맵 로딩(콜라이더 빌드), 검증/플레이 모드 공용 |
| **Result** | UI/통합 담당 | 기록 비교, 랭킹 표시 |

### 씬 전환 구조

- Boot 씬이 항상 루트에 유지되고, 다른 씬을 **애디티브 로딩**으로 얹음 (`LoadSceneMode.Additive`)
- 전환 시 이전 씬 언로드 → 다음 씬 로드. Boot의 매니저 오브젝트는 `DontDestroyOnLoad`
- 매니저 싱글턴은 Boot 소유. 각 씬은 자기 진입 시 매니저에서 컨텍스트(`MatchData`)를 읽고, 종료 시 결과를 매니저에 반납

### 소유권 규칙 (위반 시 병합 지옥)

1. **남의 씬·프리팹 수정 금지.** 필요한 변경은 소유자에게 요청
2. 공용으로 필요한 컴포넌트는 **프리팹 + API**로 납품 (예: 에디터 담당은 Play 씬을 건드리지 않고 "맵 로더" 컴포넌트를 프리팹으로 전달)
3. 네트워크 담당은 `NetService` 래퍼(Unity Multiplayer Services + NGO 캡슐화)를 싱글턴으로 전달, 통합 담당이 흐름에 배선
4. 스크립트 폴더는 기능별 분리 — 상세는 6장

## 3. 공유 데이터 계약 (개발 첫날 고정 — 이후 변경 시 전원 공유)

씬 사이를 오가는 데이터는 씬에 저장하지 않고 아래 싱글턴으로 전달한다.

```csharp
// 매치 전체 상태 — Boot 소유, DontDestroyOnLoad
class MatchData {
    VowId MyVow;            // 내 뜻
    VowId OpponentVow;      // 상대 뜻
    MapData MyMap;          // 내가 만든 맵 (검증 완료본)
    MapData OpponentMap;    // 상대가 만든 맵
    RoomSettings Settings;  // 방 설정 4종 (100_game_design.md 7.1)
    string MyNickname;      // 내 닉네임 (Lobby 입력, 기본값 "플레이어N")
    string OpponentNickname;
    float MyParTime;        // 내 맵의 검증 기록(패타임)
    float OpponentParTime;  // 상대 맵의 패타임
    PlayerRecord MyResult;  // 내 교환 플레이 결과
    PlayerRecord OpponentResult;
}

// 스트로크 1개 — 벡터 데이터 (이미지 아님!) — 203_map_editor.md
class StrokeData {
    List<Vector2> Points;   // 월드 좌표 점 목록
    float Width;            // 선 굵기
    int ColorId;            // 색상 ID (코어: 고정값, 101 색상 시스템용 예약)
}

// 맵 1개
class MapData {
    Vector2 StartPos;       // 고정값이지만 명시
    Vector2 GoalPos;        // 골 존 위치
    List<StrokeData> Strokes;
}

// 플레이 결과 — 206_ranking.md
class PlayerRecord {
    bool Cleared;
    float ClearTime;        // 초
    int AttemptsUsed;       // 시도 횟수 = 1 + R키 수동 리스폰 횟수 (낙하 자동 리스폰은 미소모)
    bool GaveUp;            // 미클리어 (시도 제한 소진 또는 플레이 시간 만료)
}

// 방 설정 — 205_network.md의 세션 프로퍼티와 1:1 대응
class RoomSettings {
    bool ParTimeMode;       // 기본 false
    int AttemptLimit;       // 무한(0) / 3 / 5
    int DrawTimeLimit;      // 초 단위 120 / 300 / 600, 기본 300
    int PlayTimeLimit;      // 초 단위 120 / 180 / 300, 기본 180 — 검증·교환 플레이 공용
}
```

**데이터 흐름**: Lobby(뜻·설정) → MatchData → Editor(맵 생성, 네트워크 전송 후 MatchData 반영) → Play(맵 로딩·결과 반납) → Result(판정)

## 4. UI 통합 방침 — 공용 UI 킷

씬이 달라도 UI가 통일감 있어야 하므로, **씬을 합쳐 작업하지 않고 공용 자산으로 통일**한다.

1. UI/통합 담당이 개발 **초반에** UI 킷을 확정·배포: 색 팔레트, 폰트, 버튼/패널/토글/인풋 프리팹
2. 각자 씬에서 **공용 킷 프리팹만** 사용 — 자체 제작 UI 금지
3. 씬 통합 후 통합 담당이 최종 일관성 패스 (간격·정렬 미세조정)
4. 화면별 UI 명세는 `204_ui.md`, 비주얼 교체 방침은 아래 5장

## 5. 아트 방침

- **당장은 기본 스프라이트(플레이스홀더)로 개발**, 아트 스타일은 후배치 가정
- 교체 가능성을 위한 규칙:
  - 시각 요소는 프리팹 내부에서만 참조 — 코드에서 스프라이트 하드지정 금지
  - 색상은 테마 ScriptableObject(색 팔레트) 참조
  - 스프라이트 교체 시 캔버스 크기·피벗·콜라이더 유지
- 유저 드로잉에도 아트 적용 가능: 스트로크는 벡터 데이터이므로 런타임 재렌더링(머티리얼/굵기), 팔레트 매핑, 카메라 후처리로 통일 가능 — `203_map_editor.md` 참조
- 필요 에셋 스펙 목록: `102_required_assets.md`

## 6. 폴더·네이밍 규칙

```
Assets/
├── Scenes/        Boot(로비·결과 UI 포함), MapEditor, Play  — Lobby/Result 는 별도 씬 대신 Boot 의 런타임 UI 로 구현됨 (필요 시 분리)
├── Scripts/
│   ├── Common/    (MatchData, RoomSettings 등 공유 타입 — 201 관리)
│   ├── Boot/      (GameFlow — 매치 FSM·로비·결과 UI. Lobby/Result 씬 역할을 현재 Boot 런타임 UI 가 겸함)
│   ├── MapEditor/ ├── Play/ ├── Network/ (NetService)
│   ├── Debug/     (AutoPilot — 개발 빌드 전용 자동 테스트)
│   └── NetTest/   (멀티 검증 전용 — 게임 코드에서 참조 금지)
├── Prefabs/       기능별 하위 폴더, 소유자별 충돌 최소화
├── Art/  UI/  Audio/
```

- 스크립트: `PascalCase` 클래스, 메서드/필드 팀 컨벤션 1개 유지 (첫 회의에서 간단히 합의)
- **`Editor`라는 이름의 폴더는 Assets 아래 어디에도 만들지 않는다** — Unity 특수 폴더(에디터 전용 어셈블리, 빌드 제외)
- 공유 타입 구현 현황: `Scripts/Common/MapData.cs`(MapConstants·StrokeData·MapData), `MapSerializer.cs`(바이너리+GZip, 청크), `MatchData.cs`(VowId·RoomSettings·PlayerRecord·MatchData 싱글턴), `StrokePalette.cs`(색상 테마 SO, `Assets/Resources/StrokePalette.asset`)
- 프리팹은 기능 폴더에 저장, 소유자만 수정

## 7. 병합(Git) 규칙

- 씬·프리팹은 **소유자만 커밋**. 타인의 씬 파일 충돌은 전부 소유자 책임으로 처리
- 커밋 단위: 기능 단위로 자주. 씬 변경 커밋 전 팀 채널 알림
- `ProjectSettings/` 변경(태그·레이어·물리 설정 등)은 전원 공유 후 단일 담당(통합 담당)만 커밋
- 공유 타입(`MatchData` 등) 수정은 전원 공유 후 반영

---
**관련 문서**: `100_game_design.md` · `202_gameplay.md` · `203_map_editor.md` · `204_ui.md` · `205_network.md` · `102_required_assets.md`