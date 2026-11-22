# M12 Wi‑Fi 신호 표시 (설계 C 표시, N11 2·3단계)

1단계(기록, `de65331`)에 이어 측정·기록 화면 타임라인과 HTML 리포트에 신호를 보인다. 홈 타임라인에는 넣지 않았다. 시험은 돌리지 않았고 빌드만 확인했다.

## 무엇을 어디에 그리나

| 자리 | 그리는 것 |
| --- | --- |
| 측정 화면 타임라인 | 측정 중: 실시간 사본(`LiveDelta.Signal`)에서 마지막 표본까지. 끝난 뒤: 다시 읽은 기록의 신호 근거 |
| 기록 상세 타임라인 | 기록의 `signal` 이벤트 → `SignalEvidenceBuilder.FromEvents` |
| 범례 (두 화면 공용 `TimelineLegend`) | 선이면 점선 견본 + "Wi‑Fi 신호 품질 (오른쪽 축 0~100%)", 값이 없으면 견본 없이 상태 글 |
| 화면 읽기 요약 | 끝에 ", Wi‑Fi 신호 품질 최저 n%"(창 안), ", 무선 어댑터 바뀜 n번", 또는 상태 글 |
| 리포트 그림 | 같은 점선(오른쪽 축), 위쪽 짧은 막대(어댑터 바뀜), 범례 8번째 칸, 설명 글에 최저·바뀜·그리지 않은 어댑터 수 |
| 리포트 관측 근거 | "Wi‑Fi 신호" 소제목 + 표 "무선 어댑터별 신호 (관측)" + 안내 문단 |

공통 계산은 Core `SignalTimeline`(`SignalTrace` = 측정 전체, `SignalFrame` = 보기 창 하나)이다. `TimelineModel.Frame(..., signal:)`이 같은 창·열 수로 신호 장면을 붙인다. 화면과 리포트는 그리기만 한다.

신호 이벤트가 없는 기록(옛 기록, 유선, 신호 공급자 없음)은 `SignalTrace.None`이다. 여백·범례 항목·요약 문구·리포트 소제목이 모두 없어 지금과 같다.

## 축과 여백

- 품질 값이 하나라도 있는 측정만 오른쪽 축 여백을 뺀다(`SignalTrace.Axis`). 이 판단은 측정 전체로 한다. 그래서 창을 옮겨도 열 수가 바뀌지 않는다.
- 화면: 여백 = "100%" 캡션 글 폭(처음 쓸 때 한 번 잼) + `ChartStyle.SignalAxisGap` 6.
  - 시간축·지연 선·띠·링크 막대·증상 표시·끝 시각("지금" 포함)을 모두 남은 그래프 영역(`PlotWidth`)에 둔다.
  - 오른쪽 축 글 "100%"(위)·"0%"(아래)는 그 오른쪽에 둔다.
  - 그래프를 누른 위치 비율도 그래프 영역 기준이다.
- **`Columns`·`FrameColumns`와의 관계**:
  - `TimelinePanel.Columns`는 이제 `round(PlotWidth)`다. 신호가 없으면 지금처럼 `round(ActualWidth)`다.
  - 화면은 장면을 만들기 전에 `timeline.SignalAxis`를 정한다(측정: 스냅숏의 `Signal.Axis`, 기록: `TimelineData.Signal.Axis`). 그 뒤 `Columns`로 `Frame`을 만들고 `Show`한다.
  - 그래서 `FrameColumns == Columns`가 그대로 지켜진다.
  - 폭이 바뀔 때 `ColumnsChanged`도 같은 비교로 그대로 동작한다.
  - 진단 값 `timelineState`에 `signalAxis`·`plotWidth`·`axisMargin`·`signal`(표시·구간·점·바뀜 수)을 더했다.
  - 배치 검사 `M5MatrixTests.TimelineProblems`와 `M12TimelineTests`는 `plotWidth`가 있으면 그것과 비교한다.
- 리포트 그림: `TimelineFigure.SignalAxisWidth` = 44.
  - 신호 선이 있으면 그래프 영역이 `PlotColumns` = 900 − 44 = 856열이다. `Frame`에도 그 열 수를 넘긴다.
  - 띠 배열·눈금·오른쪽 끝 시각 정렬(`AtRightEdge(t, PlotColumns)`)이 그 영역을 쓴다.
  - 축 글은 `PlotLeft + PlotColumns + 6`에 있다.
- 투영: 품질 → y는 `TimelineGeometry.SignalY(q, top, height)`다(100%가 위, 범위 밖은 끝에 붙임). 화면과 리포트(`TimelineFigure.SignalY`)가 같은 함수를 쓴다.

## 선과 간추리기

- 묶음 하나는 [첫 표본, 마지막 표본 + 간격)을 그린다. 다음 묶음 시작과 그 어댑터의 관측 종료에서 자른다. 품질은 계단 모양(가로선 + 세로 이음)이다.
- 묶음이 열 수보다 많으면 지연 선과 같은 규칙으로 간추린다: 열마다 최솟값·최댓값. 그래서 점은 열 수 × 2를 넘지 않는다.
- 선을 끊는 경우:
  - 정상이 아닌 상태(권한 없음·지원 안 함·연결 안 됨·읽기 실패)
  - 품질 없음
  - 이어지지 않은 묶음(`SignalRun.Continues` 거짓: 공백·넘침·번호 바뀜)
  - 어댑터 바뀜
- x는 보기 창 안으로 자른다. 측정 중에는 창 끝("지금")에서 끝난다.

## 어댑터 규칙

- 기본 경로 어댑터를 고르는 방법: 표본 수가 많은 무선 어댑터부터 차례로 본다. 이미 고른 어댑터와 관측 시간(묶음의 첫·마지막 표본)이 겹치지 않으면 고른다. 그래서 어느 시각이든 선은 많아야 하나다.
- 경로가 다른 무선 어댑터로 옮기면 선을 끊는다. 새 어댑터의 첫 표본 시각을 `AdapterChanges`에 둔다.
  - 화면: 위쪽 표시 줄의 세로 막대(신호 색, 폭 `ChartStyle.SignalChange` 3).
  - 리포트: 그래프 위쪽 짧은 막대.
- 시간이 겹치는 다른 무선 어댑터는 그리지 않는다. 범례·리포트에 이름 없이 수만 적는다("그 밖의 무선 어댑터 n개").
- 범례·요약에는 어댑터 이름을 쓰지 않는다. 리포트 표의 어댑터 칸은 링크 근거와 같은 `Redactor.Adapter`다.
  - 가림: "무선 어댑터 1"
  - 가리지 않음: "a1 (인터페이스 9)"
  - SSID·BSSID·GUID는 어디에도 없다(기록에도 없음).

## 색과 대비

| 모드 | 선 색 | 모양 | 대비 (`Contrast.Ratio`, 배경 범위는 `ContrastMatrixTests.Surface`) |
| --- | --- | --- | --- |
| 다크 | `#a78bfa` | 두께 2, 점선(대시 0·간격 2, 둥근 끝) | 바탕 `#050507` 7.48, 콘텐츠 유리 최악 4.17, 컨트롤 유리 최악 4.22, 응답 없음 띠 위 최악 3.23 |
| 라이트 | `#6d28d9` | 같음 | 바탕 `#f4f4f2` 6.45, 콘텐츠 유리 최악 6.07, 컨트롤 유리 최악 5.50, 응답 없음 띠 위 최악 4.47 |
| 고대비 aquatic | 시스템 Hotlight `#75e9fc` | 같음 | Window `#202020`와 11.50 |
| 고대비 desert | 시스템 Hotlight `#1c5e75` | 같음 | Window `#fffaef`와 6.94 |
| 리포트 문서 | `#6d28d9` (라이트와 같음) | 두께 2, `stroke-dasharray 0.1 4` 둥근 끝 | 종이 `#ffffff`와 7.10 |

- 지연 선과 겹치지 않게 했다: 인터넷 지연은 강조색 실선, 로컬 대상 지연은 Series2 파선이다. 신호는 다른 색(보라)과 점선을 함께 다르게 둔다.
- 증상(Warn)·응답 없음(Bad)·정상(Ok)·보조 글(Muted) 색과도 다르다.
- 고대비 Hotlight는 aquatic에서 강조(Highlight)와 색상이 비슷하다. 그래서 점선 모양으로 가른다.
- 토큰 위치:
  - `Palette.Signal` (init 속성. 다크·라이트 값, `SystemColorSet.ToPalette`가 Hotlight)
  - `TokenBrushes.Signal`, `DocumentBrushes.Signal`, `ReportPalette.Signal`, `ReportLook.Signal`
  - `ChartStyle.SignalLine·SignalDotOn·SignalDotOff·SignalAxisGap·SignalChange`
- 위 수치는 같은 Design 라이브러리 함수로 계산했다. 같은 기준(3:1)을 `tests/NetPulse.Design.Tests/M12SignalColorTests.cs`가 단언한다.

## 리포트 줄

- 관측 근거 절, 링크 근거 표 바로 뒤에 둔다. 신호 이벤트가 있는 기록만.
  - 표 "무선 어댑터별 신호 (관측)": 어댑터 · 품질 평균 · 품질 최저 · RSSI 평균 · RSSI 최저 · 상태.
  - 값은 `SignalAdapterEvidence.Stats`(요약이 있으면 요약, 없으면 재계산, 같은 `SignalStatistics.Compute`)에서 가져온다.
  - 상태는 상태별 시간 비율이다("읽음 99.2% · 연결 안 됨 0.8%"). 끝 요약이 없는 기록은 "(정상 종료 기록 없음, 마지막 관측까지)"를 붙인다.
  - 줄은 기본 경로 어댑터마다 하나다.
- 안내 문단:
  - 그 밖의 무선 어댑터 수
  - 어댑터 바뀜 수
  - 끊김 동안 관측한 신호(관측만): "끊김 n번 동안 관측된 Wi‑Fi 신호: 신호 품질 최저 14%, '연결 안 됨' 상태."
  - 범위 안내: "Wi‑Fi 신호는 관측 근거로만 적습니다. 끊김 판정과 '가능성이 높은 곳'은 신호 값으로 바꾸지 않습니다. …"
- 분석 판정(`Verdict`·후보)은 바꾸지 않았다. `MeasurementAnalysis`는 손대지 않았다. Core 시험이 신호를 뺀 같은 기록과 판정이 같음을 본다.
- 금지어(`ReportTexts.Forbidden`)는 생성 문구에 없다.

## 함께 고친 것 (M11 시각 검토 C3)

- **범례 잘림**: 측정·기록 화면의 가로 `StackPanel` 범례를 공용 `TimelineLegend`로 바꿨다.
  - 내부 `WrapRow` 패널이 줄 폭을 넘는 항목을 다음 줄로 넘긴다.
  - 줄보다 긴 항목(긴 상태 글)은 항목 안에서 줄을 바꾼다.
  - 150%에서 "증상 표시"가 잘리던 것이 없어진다.
- **범위 글 길이**: "1:20:00~1:20:59 · 2분"처럼 창 단계를 적던 "·" 뒤를 보이는 범위의 실제 길이로 바꿨다.
  - `MeasureScreen.RangeLength` = `TimelineModel.Seconds(to − from)`, 전체 보기는 "전체 59초".
  - 측정·기록 화면의 범위 글과 타임라인 도움말 글에 같이 쓴다.

## 검사 장면

- `tests/NetPulse.UiTests/m12-scenes.txt`의 장면을 다크·라이트 × 100%·150%로 찍는다(`M12MatrixTests`):
  - `measure-wifi-weak-done`
  - `measure-wifi-strong-done`
  - `measure-wifi-denied-done` (시험 시나리오 `wifi-denied`: 권한 없음 → 상태 글, 긴 상태 문구)
  - `records-detail-wifi` (준비 `rec-wifi`)
  - `report-preview-wifi` (그림)
  - `report-preview-wifi-evidence` (관측 근거)
- 추가 장면:
  - `m12-measure-wifi-weak-done-hc-aquatic`
  - `m12-records-detail-wifi-hc-desert`
  - `m12-measure-wifi-denied-done-hc-aquatic-150`
- 배치 검사(잘림·겹침), 타임라인 열 수 = 그래프 폭, 기대 신호 표시(선/상태 글·범례 항목)를 판정한다.
- `build/ui-test.ps1` 필수 결과 목록에 `matrix-m12-*`를 더했다. M2 매트릭스 수에서도 뺀다.

## 설계와 다른 점

1. **RSSI만 있는 측정**: 설계는 품질 선만 정했다. 품질 값 없이 RSSI만 읽힌 측정은 선을 그리지 않는다(dBm을 %로 바꾸는 근거가 없음). 범례 자리에 "품질 값 없이 RSSI만 읽혀 선을 그리지 않았습니다"를 적는다. 리포트 표에는 RSSI 평균·최저가 들어간다.
2. **연결 안 됨·읽기 실패만 있는 측정**: 설계는 권한 없음·지원 안 함만 적었다. 품질 값이 하나도 없으면 같은 방식으로 가장 많이 관측된 상태 글을 적는다(값 없음을 정상처럼 보이지 않게).
3. **리포트 범례**: 그림 범례 칸이 좁다. 그래서 선 범례는 "Wi‑Fi 신호 품질 (오른쪽 축)"로 줄였다. 어댑터 바뀜·그리지 않은 어댑터 수는 그림 설명 글과 관측 근거 절에 적는다.
4. **리포트 줄**: "무선 어댑터 줄"을 기본 경로 어댑터마다 한 줄로 했다. 겹친 다른 무선 어댑터는 설계의 "이름 없이 수만"에 맞춰 줄 없이 수만 적는다.
5. **어댑터 바뀜 장면**: 데모 네트워크는 어댑터가 하나라 UI 장면으로 만들 수 없다. Core 시험(`M12SignalDisplayTests.RouteAdapterChangeBreaksTheLineAndIsMarked`)이 끊김·표시·요약을 본다.
6. **측정 중 넓은 보기**: 칸 하나가 여러 회차를 덮을 때 다시 그리지 않는 기존 규칙(M5 bench)은 그대로다. 그래서 신호 값만 바뀐 경우 창이 한 칸 움직일 때 반영된다. 표시 방식·묶음 수·바뀜 수가 바뀌면 바로 다시 그린다.
7. **화면 읽기 요약**: 설계에 없던 신호 관측 한 마디를 요약 끝에 붙였다(신호가 관측된 측정만).
