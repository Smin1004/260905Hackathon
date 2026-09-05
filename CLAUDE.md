# 초지일관 (가칭) — 해커톤 프로젝트

Unity 6 (6000.0.66f2), 2D URP, 신 Input System, Unity Multiplayer Services + Netcode for GameObjects.

## 기획 문서가 코드보다 우선한다

기획서는 `Docs/`에 있다. **문서에 근거 없는 코드 작성 금지.** 구현 전 해당 분야 문서를 반드시 읽을 것.

필독 3종 (이 순서로):
1. `Docs/SUMMARY.md` — 프로젝트 한 장 요약
2. `Docs/100_game_design.md` — 게임 룰 (우선순위의 기준)
3. `Docs/201_common.md` — 씬 소유권, 공유 데이터 계약, 폴더 규칙, Git 규칙

이후 담당 기능 문서: `Docs/202_gameplay.md` (Play 씬) · `Docs/203_map_editor.md` (Editor 씬) · `Docs/204_ui.md` · `Docs/205_network.md` (Lobby·NetService) · `Docs/206_ranking.md` (Result 씬).
`Docs/101_extra_design.md`, `Docs/102_required_assets.md`는 스코프 판단용 참고 — 코어가 아님.

## 작업 규칙 (Docs/201_common.md 요약)

- 1인 1씬 소유: Boot·Result(통합) / Lobby(네트워크) / Editor(에디터) / Play(게임플레이). **남의 씬·프리팹 수정 금지**, 공용 기능은 프리팹 + API로 납품
- 씬 간 데이터는 `MatchData` 싱글턴(Boot, DontDestroyOnLoad)으로만 전달. 공유 타입(`Scripts/Common/`) 수정은 전원 공유 후
- 네트워크 코드는 `NetService` API만 사용 — `Unity.Services.*`, `Unity.Netcode.*` 타입을 씬 코드에 노출하지 않는다
- 스프라이트·색상은 코드 하드코딩 금지 (프리팹 참조 + 테마 ScriptableObject)
- `ProjectSettings/` 변경은 통합 담당만 커밋
- 문서 안의 **⚠ 확인 필요** 표시는 제안 상태의 수치·규칙 — 임의로 확정하지 말고 팀에 확인

## 폴더

```
Assets/Scenes/   Boot, Lobby, Editor, Play, Result
Assets/Scripts/  Common/ Lobby/ Editor/ Play/ Result/ Network/
Assets/Prefabs/  Art/  UI/  Audio/
Docs/            기획·기술 문서 (변경 시 Docs/README.md 변경 이력 갱신)
```
