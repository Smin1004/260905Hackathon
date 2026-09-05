# 그리기 게임 UI — Unity 에셋 가이드

모든 PNG는 투명 배경. Import 설정: Texture Type = Sprite (2D and UI), Filter = Bilinear, Compression = None 또는 High Quality.
디자인 기준 해상도: 1180×740 (Canvas Scaler: Scale With Screen Size, Reference 1920×1080 권장).
스케일: buttons/icons/markers/tooltips @3x, panels/timer/overlay @2x, tiles @2x → Pixels Per Unit 또는 RectTransform 크기 조정으로 1x 환산.

## 폴더
- panels/   9-slice 패널 배경 (Image Type: Sliced)
- tiles/    반복 배경 타일 (Wrap Mode: Repeat, Image Type: Tiled)
- buttons/  버튼 상태별 배경 (Button > Sprite Swap 또는 Transition)
- icons/    흰색 라인 아이콘 → Image Color로 틴트
- markers/  시작점(S)·골(G) 마커, 펄스 링, 소형 도트
- timer/    타이머 링 (Image Type: Filled, Radial 360, Origin Top, Clockwise)
- tooltips/ 툴팁·필·배지·칩 (텍스트 포함 버전 + 9-slice 빈 버전)
- overlay/  대기 오버레이 레퍼런스 합성 이미지

## 9-slice Border 값 (1x 기준 px → 캡처 스케일 곱하기)
- panel_dark: 18 → @2x 36
- panel_paper_canvas: 24 → @2x 48
- panel_card_overlay: 64 (그림자 포함) → @2x 128
- panel_chip_light: 12 → @3x 36
- btn_tool_*: 14 → @3x 42
- btn_verify_*_blank: 좌우 18 / 상 18 / 하 40 (그림자·바닥 포함) → @3x ×3
- btn_dark_blank: 12 → @3x 36
- badge_round_blank: 14 → @3x 42
- tooltip_*_blank: 좌우 20 / 상 16 / 하 34 (꼬리 포함, 꼬리는 중앙) → @3x ×3
- pill_*_blank: 좌우 = 높이/2

## 상태 매핑
툴 버튼 (펜/실행 취소/전체 지우기/골 배치)
- Normal: btn_tool_normal + icon 흰색
- Highlighted: btn_tool_hover
- Selected(펜): btn_tool_active + icon 색 #1B2A3E
- Selected(골): btn_tool_active_goal + icon 흰색
- Disabled: btn_tool_disabled + icon 색 #7F8797

검증하기 버튼
- 골 미배치: btn_verify_disabled(_blank) — 호버 시 tooltip_goal_required 표시
- 골 배치: btn_verify_active(_blank), Pressed: btn_verify_pressed_blank (Y +2px 이동)
- 클릭 → overlay_dim(전체 스트레치, blur 선택) + panel_card_overlay + badge_clear + "상대 대기 중" + dot_loading ×3 (bounce, 0.2s 딜레이)

타이머
- timer_ring_track 아래, timer_ring_fill_normal 위 (Filled Radial360, fillAmount = 남은/전체)
- 남은 시간 ≤ 10s: timer_ring_fill_warning 으로 스왑, 텍스트 색 #F26B5B 계열

시작점 / 골
- marker_start: 캔버스 왼쪽 하단 고정 (anchor 7.5%, 88%). marker_pulse_start를 뒤에 두고 Scale 1→2.4, Alpha .6→0, 1.8s loop
- 시작점 호버 → tooltip_start ("시작점은 왼쪽 하단 고정입니다"), 라운드 시작 후 3.5초간 자동 노출
- marker_goal: 클릭 위치에 배치, marker_pulse_goal Pop(scale .5→1, .3s)
- 골 배치 모드 중 캔버스 상단 중앙에 pill_goal_mode 표시

## 컬러 (sRGB)
- 배경: #1F2A3F (color_bg)  ·  패널: #2E3A52  ·  패널 테두리: #46536E
- 캔버스 종이: #FBF9F3
- 그린(검증/클리어): #6EE07B  ·  그린 섀도: #45AF57
- 코랄(골/경고): #F26B5B  ·  틸(시작점): #5FC9C0
- 텍스트: #FFFFFF / 보조 #B8C0D0 / 카드 위 텍스트 #1B2A3E / 카드 보조 #6B7385

## 폰트 (TextMeshPro)
- 헤드라인·숫자·버튼: Jua (Google Fonts, OFL)
- 본문·툴팁·라벨: Noto Sans KR 400/500/700 (Google Fonts, OFL)
- 사이즈(1x): 제목 32, 타이머 26, 라운드 30, 검증 버튼 24, 오버레이 제목 40, 툴팁 14, 툴 라벨 11

## 문구
- 시작점 툴팁: "시작점은 왼쪽 하단 고정입니다"
- 검증 비활성 툴팁: "골을 배치해야 검증할 수 있습니다"
- 골 모드 필: "캔버스를 클릭해 골을 배치하세요"
- 대기 오버레이: "검증 클리어!" / "상대 대기 중" / "상대가 경로를 완성하면 다음 라운드가 시작됩니다"
