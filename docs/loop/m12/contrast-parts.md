# M12 부품·카드 본문 글 화면 대비 실측과 비활성 단추 (설계 F)

DECISIONS "M11 G4 2라운드"(부품·카드 본문 글은 화면에서 잰 적이 없음)와 "M12 비활성 단추 대비"(사용자 결정: 비활성 글은 4.5:1 예외, 3:1 이상, 단추 전체 흐림 대신 비활성 글·테두리 토큰)를 구현했다. 시험은 돌리지 않았고 빌드만 확인했다(App·Design.Tests·UiTests 경고 0·오류 0). 실측 값은 노트북 게이트의 `g5/contrast-m12-*.json`에서 나온다.

## 1. 화면 실측 (`tests/NetPulse.UiTests/M12ContrastTests.cs`)

방식은 M11과 같다: 글자 있음/없음(`--hide-measured-text`) 두 장, 숨긴 캡처의 바탕과 토큰 글자 색의 칸별 10백분위 대비, 기준 미달 연결 영역 12 px 이하. 장면마다 `Required`의 요소가 모두 화면 안에 있어야 한다(빠지면 실패). M11ContrastTests의 단언은 바꾸지 않았고 요약 주석만 이 시험을 가리키게 고쳤다.

숨김 고리(`--hide-measured-text`에서 내용만 투명, 바탕·테두리는 그대로):

| 부품 | 숨기는 것 | 새 자동화 ID |
| --- | --- | --- |
| `GlassButton` | 글·아이콘 (활성·비활성 모두) | 없음 (단추 ID로 찾고 내용 상자를 잰다) |
| `StatusChip` | 상태 아이콘·글 | `<칩 id>.label` |
| `ScreenParts.InfoChip` | 라벨·값 | `<칩 id>.label` (값은 기존 `.value`) |
| `Banner` | 상태 아이콘·글 | `<배너 id>.text` |
| 측정 타임라인 범위 글 | 글 | 기존 `measure.timeline.range` |

단추는 UIA에서 Button 하나라, 단추 경계에서 좌우 `Space.S5`를 빼고 줄 높이(Label 줄 높이와 IconM 중 큰 값)만큼 가운데를 잰다. 아이콘 단추는 가운데 IconM 칸. 측정이 상자를 4 px 넓히므로 넓힌 상자가 단추 바탕·둥근 모서리 안에 머문다.

| 장면 (다크·라이트) | 요소 (부품) |
| --- | --- |
| `measure-done` | `measure.start`(강조), `measure.mark`(유리, 비활성), `measure.config`·`measure.now`(유리), `measure.zoomout`·`measure.all`(아이콘), `measure.timeline.range`(카드 본문) |
| `measure-startfail` | `measure.banner.starterror.text`(오류 배너), `measure.start`(강조), `measure.mark`(유리, 비활성) |
| `firstrun-detected` | `firstrun.virtual.text`(주의 배너), `firstrun.gateways.text`(정보 배너), `firstrun.start`(강조), `firstrun.edit`(유리) |
| `profile-locked` | `profile.locked.text`(정보 배너), `profile.back`(유리), `profile.cancel`·`profile.add`·`profile.remove`(고스트, 비활성), `profile.save`(강조, 비활성) |
| `records-list` | `records.row.0.end.label`·`records.row.1.end.label`(상태 칩), `records.compare`(유리) |
| `settings-about` | `settings.about.{version,build,license}.label`(정보 칩 라벨)·`.value`(값), `settings.about.legal`(유리) |

글자 색과 기준: 강조 단추 AccentInk, 유리·고스트 단추·칩 글·칩 값·배너 글 Ink, 칩 라벨·카드 본문 Muted — 4.5 (큰 글이면 3.0, `TypeStyle.IsLarge`; 이 장면들에는 큰 글이 없다). 아이콘 단추 Ink 3.0(그래픽). 비활성 단추(실행 중 UIA `IsEnabled`로 판단)는 종류와 관계없이 DisabledInk 3.0. 결과: `g5/contrast-m12-<장면>-<테마>.json`, `build/ui-test.ps1` G5 필수 목록에 12개를 더했다.

## 2. 비활성 단추 (`GlassButton`, 토큰 `Palette.DisabledInk`·`DisabledEdge`)

- 단추 전체 불투명도 0.45(약 1.4~1.7:1)를 뺐다. 비활성이면 글·아이콘은 `DisabledInk`, 테두리는 `DisabledEdge` 점선(`Size.DisabledDashOn/Off` 3·2), 주요 단추는 강조색 칠 대신 보통(컨트롤) 유리 바탕. 보조 단추는 유리 그대로, 고스트·아이콘은 투명 그대로.
- 활성과의 구분: 주요는 채움(강조색 → 유리), 모든 종류에 점선 테두리(활성에는 없음). 글 색도 본문색보다 휘도가 확실히 다르다(아래 "본문색과").
- 고대비: 값을 두지 않아 `Disabled` = 시스템 GrayText. 유리는 고대비에서 Window 색.
- 입력 칸의 비활성 글(`Palette.Disabled`, 그 밖에서는 Muted)은 바꾸지 않았다.

| 테마 | DisabledInk | DisabledEdge | 콘텐츠 유리 | 컨트롤 유리 | 대화상자 유리 | 불투명 표면(최저) | 본문색과 |
| --- | --- | --- | --- | --- | --- | --- | --- |
| 다크 | `#8a8a94` | `#6b6b75` | 3.32 (테두리 2.15) | 3.36 (2.18) | 3.71 (2.41) | 4.14 (컨트롤) | 3.11 |
| 라이트 | `#71717a` | `#9a9aa3` | 4.13 (2.39) | 3.74 (2.16) | 3.55 (2.05) | 3.91 (컨트롤) | 3.90 |
| 고대비 aquatic | GrayText `#a6a6a6` | 같음 | Window 위 6.69 | | | | |
| 고대비 desert | GrayText `#676767` | 같음 | Window 위 5.43 | | | | |

유리 값은 `ContrastMatrixTests.Surface`와 같은 가장 불리한 범위 모델(정상·끊김 × 오로라, 결 포함)이다. Design 시험 `DisabledButtonContrastTests`: 글 3.0 이상(유리 세 단계와 불투명 표면), 테두리 2.0 이상(비활성 테두리는 WCAG 1.4.11 예외지만 모양 신호라 보이게 둔 설계 판단), 본문색과 2.5 이상, 고대비 GrayText.

## 3. 토큰 계산만 남은 것과 이유

- 화면 머리처럼 유리 없이 바탕 위에 놓인 비활성 고스트 단추(`profile.cancel`): 바탕 범위의 계산상 최악(빛·오로라가 모두 겹친 밝은 점)에서는 Muted도 4.5가 안 되는 모델이라 토큰 보장이 없다. 화면 실측(`profile-locked`)으로만 판정한다. 비활성 주요·보조 단추는 유리 바탕이 있어 계산 보장이 있다.
- 칩·배너의 상태 아이콘은 재지 않는다(숨김 고리는 바탕을 깨끗이 찍으려고 아이콘도 숨길 뿐, 상태색 아이콘 3:1은 `ContrastMatrixTests` 계산). 실측은 다크·라이트 100%만이고(M11과 같음) 고대비·150%는 Design 시험의 계산만.
- 기록 타임라인 범위 글 `records.timeline.range`: `RecordsScreen.cs`가 다른 작업(프레임 큐)으로 수정 중이라 숨김 고리를 달지 않았다. 측정 화면 범위 글과 같은 부품(카드 유리 위 Caption Muted)이다.
- 단추가 아닌 비활성 컨트롤(`SettingToggle` 리포트 "증상 메모 넣기", 진단 카드 펼침, 기록 비교 고르기 중 못 고르는 줄 `RowButton`, `SegmentedChoice` 읽기 전용)은 그대로다. 스위치는 여전히 불투명도 0.45로 흐린다. 이번 결정의 범위(단추) 밖이라 남긴다.
- 고스트·아이콘 단추의 비활성은 채움 차이가 없고 점선 테두리와 글 휘도로 구분한다.

## 4. 확인할 것 (노트북 게이트)

- `M12ContrastTests` 12건이 통과하는지, 특히 단추 안 칩(`records.row.N.end.label`)이 UIA에 Text로 드러나는지(안 드러나면 필수 누락으로 실패).
- 매트릭스 캡처에서 비활성 단추 모양(점선, 주요 단추 유리 바탕)이 바뀐다: `m5-measure-done`, `m5-profile-locked`, `m5-profile-invalid`, `m8-report-exporting`, 설정 진단 저장, 고대비 저장 단추.
