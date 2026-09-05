# 102 — 기획 구현에 필요한 준비 사양 (에셋 목록)

> 기획(`100_game_design.md`)을 실제로 구현하려면 아래 에셋들이 필요하다.
> **원칙**: 아트 스타일은 후배치 가정 — 당장은 플레이스홀더(기본 스프라이트·단색)로 개발하고, 형태·크기 스펙을 지켜 교체만으로 아트 적용 가능하게 만든다. 방침 상세는 `201_common.md`.

## 1. 필수 에셋 (MVP 동작에 필요)

### 1.1 캐릭터

| 에셋 | 사양 | 플레이스홀더 |
|---|---|---|
| 플레이어 스프라이트 | **적용됨** (2026-09-06) — `Assets/Art/Player/player_sheet.png` 4×4 시트(칸 300px): 1행 Idle 4 / 2행+3행 앞 2칸 점프 6(상승 3·하강 3) / 3행 뒤 2칸 잉여 / 4행 Walk 4. [Chojiilgwan > Build Player Sprites] 가 슬라이스 + `Resources/Art/PlayerSpriteSet.asset` 생성 (PPU = idle 높이 ÷ 0.9u, 피벗 = 발). `PlayerController` 가 상태별 프레임 선택(상승·하강은 속도 진행도, Walk 는 속도 비례 fps). 세트가 없으면 사각형 플레이스홀더 | (교체 완료) |
| 리스폰 이펙트 | 없어도 동작 가능 (스코프 아웃 가능) | 화면 깜빡임 코드 처리 |

- 요구사항: 좌우 방향 전환이 가능한 형태 (스프라이트 플립)
- 앵커: 발바닥 중심 — 콜라이더 하단 기준으로 배치

### 1.2 맵/배경

| 에셋 | 사양 | 플레이스홀더 |
|---|---|---|
| 플레이 배경 (Play 씬) | 1920×1080 커버 가능한 PNG 1장 | 카메라 배경 단색 |
| 에디터 캔버스 배경 | 격자(그리드) 텍스처 — 그리기 좌표감 제공 | 유니티 그리드 라인 또는 단색 |
| 시작점 마커 | 작은 아이콘 (화살표/기둥), 64×64 | 단색 스프라이트 + 텍스트 라벨 |
| 골 존 표시 | 깃발/문 모양 아이콘, 64×64, 닿는 판정 범위 시각화 | 노란색 반투명 사각형 |

### 1.3 UI

| 에셋 | 사양 | 플레이스홀더 |
|---|---|---|
| 버튼 (일반/눌림/비활성 3상태) | 9-slice 가능한 PNG, 200×60 기준 | Unity 내장 UI Sprite |
| 패널/대화상자 배경 | 9-slice PNG | 반투명 단색 패널 |
| 토글/체크박스 | ON/OFF 2상태 (방 설정용) | Unity 내장 Toggle |
| 텍스트 입력창 | 방 코드 입력용 (9-slice 필드) | Unity 내장 InputField |
| **폰트 (한글 필수)** | 한글 지원 무료 폰트 1종 — 예: Noto Sans KR, 나눔고딕 (라이선스 확인) | Unity 기본 폰트는 한글 깨짐 — **반드시 교체** |

### 1.4 에디터 전용

| 에셋 | 사양 | 플레이스홀더 |
|---|---|---|
| 펜/골/취소 툴 아이콘 | 48×48 PNG 각 1개 | 텍스트 라벨 버튼 |
| 브러시 커서 | 드로잉 중 표시용 (선택) | 기본 커서 |

## 2. 선택 에셋 (시간 남으면 — `101_extra_design.md` 연동)

| 에셋 | 용도 |
|---|---|
| 색상 팔레트 아이콘 (검/하늘/노랑) | 색상 시스템 도입 시 |
| 바운스/얼음/위험 표시 텍스처 | 스트레치 색 도입 시 |
| 골 클리어 이펙트 | 파티클 1종 |
| 점프/낙하/리스폰 이펙트 | 파티클 또는 스프라이트 애니메이션 |

## 3. 오디오 — **구현됨** (2026-09-06)

파일은 `Assets/Audio/`, 연결은 `Assets/Resources/Audio/SoundBank.asset`(ScriptableObject — 클립·볼륨·페이드). 메뉴 [Chojiilgwan > Build SoundBank]가 파일명 규약으로 자동 연결한다. 재생은 `Sound` 정적 API(`Scripts/Common/Sound.cs`)만 사용 — 씬 코드에서 AudioSource 를 직접 만들지 않는다.

| 파일 | 용도 | 호출 위치 |
|---|---|---|
| `bgm_lobby_edit` | 배경음 — 로비·뜻 선택·에디터(검증 포함)·결과 | `GameFlow.SetState` (크로스페이드), 에디터 씬 단독 실행 시 `MapEditorController.Start` |
| `bgm_battle` | 배경음 — 교환 플레이(결과 대기 포함) | `GameFlow.SetState` |
| `sfx_click` | 버튼 클릭 공통 | `RuntimeUI.Button`, `MapEditorHud.Hook`·스와치, `PlayHud` 기권 버튼 |
| `sfx_jump` / `sfx_land` | 점프 / 착지 (착지 속도에 따라 볼륨) | `PlayerController` |
| `sfx_confirm` | 확정됨 — 뜻 확정, 맵 제출 | `GameFlow.ConfirmVows`, `MapEditorController.Complete` |
| `sfx_drawing` (루프) | 펜으로 그리는 동안 | `MapEditorController.BeginStroke/EndStroke` |
| `sfx_eraser` (루프) | 지우개 드래그 동안 | `MapEditorController.BeginErase/EndErase` |
| `sfx_clock` (루프) | 타이머 마지막 10초 | `MapEditorHud.UpdateTimer`, `PlayHud` 타이머 |

- 사망음은 에셋이 없어 `PlayerController` 절차 생성음 유지. 골 클리어음은 미구현 (에셋 추가 시 `SoundBank` 필드 + `PlaySession` 클리어 지점에 한 줄)
- 라이선스: 팀 제작/제공 파일 (출처 확인 필요 시 팀에)

## 4. 외부 의존성 (에셋이 아닌 준비물)

| 항목 | 내용 |
|---|---|
| Unity 6 (6000.0.66f2) | 팀 전원 동일 버전 고정 |
| Input System (신) | 패키지 설치됨 — 액션은 `InputSystem_Actions.inputactions`에 정의 (`202_gameplay.md` 1장) |
| 2D URP 템플릿 | 프로젝트 생성 시 선택 |
| **Unity Multiplayer Services + NGO** | 패키지 `com.unity.services.multiplayer`, `com.unity.netcode.gameobjects`, `com.unity.transport`, 테스트용 `com.unity.multiplayer.playmode`. 팀 대표 1명이 Unity Cloud 프로젝트 연결 후 `ProjectSettings/` 커밋 — 나머지는 풀만 (`205_network.md` 2장) |
| 한글 폰트 에셋 | TextMeshPro용 폰트 에셋 생성 필요 (아틀라스 생성) |

## 5. 에셋 교체를 위한 스펙 규칙

- 아트 후배치를 전제로 하므로, 아래를 지키면 스프라이트 교체만으로 테마 변경됨:
  - 모든 시각 요소는 **프리팹 내부에만 참조** — 코드에서 직접 스프라이트 지정 금지
  - 캐릭터/오브젝트 피벗·콜라이더 크기는 플레이스홀더 기준으로 고정, 교체 아트는 같은 캔버스 크기로 제작
  - 색상은 코드 하드코딩 대신 **테마 ScriptableObject(색 팔레트)**에서 참조 — `201_common.md`의 UI 킷 규칙과 동일
- 폴더 구조: `Assets/Art/`(캐릭터·맵) / `Assets/UI/`(UI 킷) — 상세는 `201_common.md`

---
**관련 문서**: `201_common.md` · `204_ui.md` · `101_extra_design.md` · `100_game_design.md`