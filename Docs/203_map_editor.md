# 203 — 맵 에디터 구현 (MapEditor 씬)

> 드로잉 툴 → 스트로크 벡터 데이터 → 물리 오브젝트 파이프라인. 소유: 에디터 담당.
> Play 씬은 건드리지 않고 **맵 로더 컴포넌트(프리팹+API)**로 납품한다 — `201_common.md` 소유권 규칙.

## 1. 에디터 화면 구성

| 요소 | 내용 |
|---|---|
| 캔버스 | 고정 크기 드로잉 영역 (월드 좌표와 1:1 대응) |
| 시작점 | **고정** — 캔버스 왼쪽 하단에 앵커 표시, 편집 불가 |
| 기본 경계 | **바닥·왼쪽 벽**은 고정 콜라이더 — 캔버스에 표시되지만 편집 불가. **천장·오른쪽은 개방** (그 밖은 낙하 판정) |
| 툴 바 | 펜 (**굵기 3단계 · 색상 팔레트**) / **지우개** (드래그한 구간을 잘라내 스트로크 분할, 반지름은 굵기 단계와 연동) / 실행취소·다시실행 (스냅샷 방식 — 그리기·지우기·골·전체지우기 모두 1단계씩. **Ctrl+Z / Ctrl+Y**, Ctrl+Shift+Z) / 전체 지우기 / **골 배치** / **▶ 검증 플레이** / 완료 |
| 골 배치 | 골 버튼 → 캔버스 클릭 시 그 위치에 골 존 배치 (재클릭으로 이동) — 골 1개만, **필수** (자동 배치 없음) |
| 타이머 | 그리기 시간 제한 표시 (방 설정값 — `100_game_design.md` 7.1) |
| 제출 | 골 배치 + 검증 클리어 완료 시 활성화 |

- 코어에서는 색상 시스템 없이 "선 = 벽, 골 배치 버튼"만. 색상 팔레트 도입 시 구조는 `101_extra_design.md` 참조

## 2. 스트로크 데이터 (핵심 설계)

스트로크는 **이미지가 아니라 벡터 점 목록**으로 저장한다 — 이것이 콜라이더 변환과 아트 재렌더링을 모두 가능하게 하는 핵심 결정.

```csharp
class StrokeData {
    List<Vector2> Points;  // 월드 좌표 (순서 유지)
    float Width;           // 선 굵기 — 프리셋 0.15 / 0.3 / 0.6u 중 하나 (MapConstants.PenWidths)
    int ColorId;           // 팔레트 ID (StrokePalette SO: 0 검정 / 1 하늘 / 2 노랑 / 3 초록 / 4 파랑 / 5 빨강). 코어 로더는 모든 색을 벽으로 취급, 의미 부여는 101
}
```

## 3. 드로잉 파이프라인

```
입력(마우스/터치) → 월드 좌표 변환 → 점 수집 → 스트로크 확정 → 저장
```

1. **입력**: 캔버스 영역에서 드래그 시작 → 스트로크 시작
2. **좌표 변환**: 화면 좌표 → 월드 좌표 (캔버스는 Orthographic 카메라 기준 고정 사각형)
3. **점 수집**: 최소 이동 거리(예: 0.1u) 이상 움직였을 때만 점 추가 — 데이터량 제어
4. **렌더링**: `LineRenderer`로 즉시 표시 (선 끝 라운드 캡)
5. **확정**: 드래그 종료 → `StrokeData` 리스트에 추가

### 맵 제한값 (제안 — 테스트 후 조정)

| 항목 | 값 |
|---|---|
| 캔버스(맵) 크기 | 가로 30u × 세로 15u |
| 스트로크 수 상한 | 60개 |
| 스트로크당 점 상한 | 300점 |
| 골 배치 | 필수 — 그리기 시간 만료 후 골 미배치면 **골 배치만 허용되는 상태**로 전환 (`100_game_design.md` 5장) |
| 선 굵기 | 프리셋 3단계 0.15 / 0.3 / 0.6u. 콜라이더 `edgeRadius` = 굵기 ÷ 2 |
| 지우개 반지름 | 0.3 / 0.6 / 1.0u (굵기 단계와 짝) — 선 굵기의 절반을 더해 "보이는 선"이 지워지게 |
| 골 최소 거리 | 시작점에서 3u 이상 (패타임 0.2초짜리 맵 방지) |
| 실행취소 깊이 | 50단계 |

## 4. 스트로크 → 물리 변환 (맵 로더 컴포넌트)

**에디터 담당이 납품하고 Play 씬(게임플레이 담당)이 사용하는 컴포넌트.**

```csharp
interface ILoadableMap {
    void Load(MapData map);   // 콜라이더 + 렌더러 + 기본 경계(바닥·왼쪽 벽) + 골 존 생성
    void Unload();
    MapData Current { get; }
    GoalZone Goal { get; }    // Goal.Reached 이벤트로 클리어 판정
}
// 구현: Scripts/MapEditor/MapLoader.cs (옵션: BuildBoundaries / BuildGoal / BuildColliders)
```

- 스트로크 점 목록 → `EdgeCollider2D` (인접 점 2개씩 에지로 구성) — 선이 곧 벽
- 시각 표현: `LineRenderer`로 점 목록 재렌더링 (머티리얼·굵기는 테마 참조 — `201_common.md`)
- 채우기(면) 물리는 **처음부터 스코프 아웃** — 선 물리만으로 게임 성립
- 생성/파괴는 런타임 프리팹 인스턴스로 처리, 씬 파일 오염 없음

### 성능 고려

- 스트로크당 오브젝트 1개 (EdgeCollider + LineRenderer), 60개 × 300점 수준에서 문제없도록 점 간격 유지
- 점 다운샘플: 수집 시 최소 거리 필터 + 필요시 RDP(Douglas-Peucker) 단순화 1회

## 5. 직렬화 & 전송

- `MapData` → **양자화 바이너리(점당 4B: int16 x,y × 0.01u) + GZip** → 4KB 청크 → 네트워크 전송 (`205_network.md` 5장). 구현: `Scripts/Common/MapSerializer.cs` (`Serialize`/`Deserialize`, `MapChunker.Split`, `MapChunkAssembler`)
- JSON(`JsonUtility`)은 점당 ~20B라 전송에 쓰지 않고 디버그 로그용으로만 둔다
- 크기: 상한(60 × 300점)에서 압축 전 ≤ 72KB, 압축 후 보통 그 절반 — **전송 목표 ≤100KB** 안. 초과 시 에디터 UI에 경고
- NGO 메시지는 기본 최대 페이로드가 수 KB라 `NetService`가 4KB 청크로 분할 전송한다 (`205_network.md` 5장). 청크 수를 25개 이내로 두기 위해 위 상한 + 아래 양자화 + 다운샘플로 목표 크기 유지. 초과 시 RDP 단순화 강제
- 좌표는 소수 둘째 자리에서 반올림(양자화)해 데이터량 절감. **양자화는 스트로크 확정 시점(펜을 뗄 때) 즉시 적용** → 검증 플레이의 물리와 상대가 받는 전송본이 동일
- 완료 버튼은 직렬화 → 역직렬화 왕복이 원본과 일치하는지 검사한 뒤에만 `MatchData.MyMap`에 반영하고 `Completed(map, payload)` 이벤트를 낸다 — `NetService.SendMap`이 이 이벤트를 구독

## 6. 검증 플레이와의 연계

- 에디터의 **[▶ 검증 플레이]** → 현재 맵을 `MapData`로 확정 → **MapEditor 씬 안에서 `PlaySession`을 띄워** 검증 (씬 전환 없음 → 그리던 상태·실행취소 기록 유지). 구현: `MapEditorController.StartVerification()` — `202_gameplay.md` 8장
- 검증 중 ESC 또는 [에디터로 돌아가기] 버튼으로 언제든 복귀 (맵 수정 목적, 갇혔을 때 탈출). R 키는 시작점 리스폰
- **골 도달 = 검증 성공** → 클리어 시간이 패타임. [완료] 버튼은 검증 성공 상태에서만 활성화되며, 맵을 수정(그리기·지우기·골 이동·실행취소)하면 검증이 무효가 되어 다시 클리어해야 한다
- 검증 실패 → 에디터 복귀, 맵 수정 후 재검증
- 검증 플레이에는 별도 시간 제한이 없다 — 라운드의 **그리기 시간**이 검증까지 포함한 상한이며 만료 시 제출 실패로 처리된다 (2026-09-06 확정). (구 규칙: 플레이 시간 제한 적용 — 폐기) 참고: 맵 수정 후 재검증 (타이머 새로 시작). 시도 제한은 없음. 그리기 시간(방 설정)은 검증 중에도 흘러 검증·완료까지 포함한 상한이며, **만료 시 미제출 = 제출 실패 = 그 라운드 패배** (`100_game_design.md` 6장). 그리기 시간 만료 시 검증 플레이 중이면 강제 종료 후 에디터 잠금
- 검증 클리어 → [완료] → **제출 확정** (에디터 잠금, 수정 불가) → 패타임 기록, 상대 대기

## 7. 아트 적용 (후배치 대비)

- 스트로크가 벡터 데이터이므로 아트 스타일 적용 가능:
  1. 런타임 재렌더링 — LineRenderer 머티리얼/굵기/테두리 교체
  2. 색 → 머티리얼 매핑 (`ColorId` 기반, 101 색상 시스템과 연결)
  3. 카메라 후처리로 전체 톤 통일
- 손그림 거친 느낌을 아트 스타일로 채택하는 방안도 후보 (`101_extra_design.md`)

## 8. 테스트 체크리스트

- [ ] 그린 선이 정확히 그 위치에 벽으로 생성되는지 (좌표 변환 검증)
- [ ] 시작점 고정 + 골 이동 배치 정상 동작
- [ ] 직렬화 → 역직렬화 → 콜라이더 재생성 왕복 무손실
- [ ] 실행취소/전체지우기 후 전송 데이터 정합성

## 9. 구현 현황 (2026-09-05)

| 파일 | 역할 |
|---|---|
| `Scenes/MapEditor.unity` | 에디터 씬 — Main Camera + `MapEditor` 오브젝트(`MapEditorController`) + **`MapEditorHud` 프리팹 인스턴스**. 캔버스 시각·이벤트시스템은 런타임 생성 |
| `Scripts/MapEditor/MapEditorController.cs` | 도구 상태·입력(신 Input System `Pointer.current`)·실행취소·완료. 모든 조작이 public 메서드(`BeginStroke/AddPoint/EndStroke`, `EraseAt`, `SetGoal`, `Undo`, `ClearAll`, `Complete`)로 노출되어 UI·테스트가 같은 경로 |
| `Prefabs/UI/MapEditorHud.prefab` + `Scripts/MapEditor/MapEditorHud.cs` | **에디터 HUD** (2026-09-06, 시안 기준). 상단: 라운드 배지·안내문(도구/상태별 문구)·남은 시간 링. 오른쪽: 펜/지우개/실행취소/전체지우기/골 배치. 하단: 굵기 3단·색상 팔레트(StrokePalette 항목 수만큼 생성)·상태 칩·[검증하기]·[완료]. BackCanvas(Screen Space Camera, 정렬 -100)가 배경 도트와 종이 프레임을 스트로크 뒤에 그리고, `PaperSlot` 영역을 뷰포트로 바꿔 카메라를 맞춘 뒤 맵 사각형을 투영해 종이 프레임을 덮는다 → 해상도 무관하게 UI 와 맵이 일치. 컨트롤러는 같은 씬에 HUD 가 있으면 Bind, 없으면 아래 플레이스홀더 |
| `Scripts/MapEditor/HudToolButton.cs` | 선택/비활성 상태별 배경 스프라이트·아이콘 색 교체 버튼 (프리팹 참조만 채움) |
| `Scripts/MapEditor/MapEditorTheme.cs` → `UI/MapEditorTheme.asset` | 에디터 화면 테마 SO (색상 + 캔버스 마커·도트 타일 스프라이트). CanvasView 와 HUD 가 공유 — Docs/201 5장 |
| `Scripts/MapEditor/MapEditorHudBuilder.cs` | 에디터 전용(`#if UNITY_EDITOR`) 프리팹 생성기. 메뉴 **[Chojiilgwan > Build MapEditor HUD]** = `Assets/image` 스프라이트 임포트 설정(Single·9-slice 보더·타일 Repeat) → 테마 에셋 → 프리팹 덮어쓰기 → MapEditor 씬 배치. 레이아웃 수치는 이 파일에 있음 (프리팹을 인스펙터에서 고친 뒤 재실행하면 덮어써짐) |
| `Assets/image/` | HUD 이미지 에셋 52장 (패널·버튼 상태·아이콘·마커·타이머 링·타일·툴팁). `ref_` 접두사 3장은 글자가 구워진 참고용 — 씬에 쓰지 않는다 |
| `Scripts/MapEditor/MapEditorUI.cs` | 런타임 UI (플레이스홀더 — HUD 프리팹이 씬에 없을 때만 사용) |
| `Scripts/MapEditor/StrokeGeometry.cs` | 양자화, RDP 단순화, 점 상한 강제, 원으로 잘라내기(지우개) — 순수 함수 |
| `Scripts/MapEditor/StrokeVisual.cs` | 스트로크 → LineRenderer(+EdgeCollider2D). 에디터와 Play가 같은 코드 사용 |
| `Scripts/MapEditor/MapLoader.cs` | `ILoadableMap` 구현 + `GoalZone` — Play 씬 납품 컴포넌트 |
| `Scripts/MapEditor/CanvasView.cs` | 배경·격자·경계·시작점·골 마커, 카메라 맞춤 (상·하단 UI 바 비율을 빼고 남은 영역에 캔버스를 맞춰 UI 가 드로잉 영역을 가리지 않음) |

미구현: 제출 후 대기 오버레이의 HUD 버전(현재는 Boot 의 런타임 오버레이 — 에셋 `panel_card_overlay`·`chip_*` 준비됨), 다시실행 버튼(단축키 Ctrl+Y 만), 지우개 아이콘 에셋(기본 스프라이트 임시). 그리기 타이머 만료 처리(제출 실패 → 라운드 패배)와 검증 시 상대 뜻 적용은 구현됨 (2026-09-06). HUD 라운드 배지는 라운드 번호만 표시한다 (`totalRounds` = 0; 값을 주면 "n / N") — 라운드 수 상한은 없다 (`100_game_design.md` 3장).

---
**관련 문서**: `202_gameplay.md` · `205_network.md` · `101_extra_design.md` · `201_common.md` · `100_game_design.md`