# M12 G1-B 캡처 전 상태 기반 대기

설계: `docs/loop/M12-design.md` G1 "B [1-1]", "그리기 끝남" [2-4].
매트릭스·G5 캡처 앞의 고정 대기 `Thread.Sleep(700)`(진행도 고정 전환 장면은 `Thread.Sleep(500)`)을 상태 기반 대기로 바꿨다.
빨라졌는지는 아직 재지 않았다. 노트북에서 실측한 뒤에만 말한다.

## 앱: "그리기 끝남" (`diag.state`의 `settle`)

```
settle = { pendingWork, finiteMotions, heldMotions, generation, sceneGeneration, laidOut, timelineOk, settled }
settled = pendingWork == 0 && finiteMotions == 0 && laidOut && timelineOk
```

계산: `src/NetPulse.App/Design/DrawSettle.cs`. 셸은 `AppShell.Diagnostics.cs`의 `SettleDiag`, 쇼케이스는 `ShowcaseDiagnostics.cs`의 `SettleDiag`.

### pendingWork: 예약된 그리기 작업 수

| 항목 | 늘어나는 곳 | 줄어드는 곳 |
|---|---|---|
| 셸 렌더 큐 | `AppShell.QueueRender`에서 `renderQueued = true` (1로 셈) | 큐의 작업이 시작할 때 `renderQueued = false` |
| 공통 요소 이동 대기 | `ScreenHost.Show`에서 `target.LayoutUpdated`를 걸 때 `sharedPending++` | 그 처리기(`Once`)가 돌 때 `sharedPending--` |
| 기록 화면 프레임 목록 | `RecordsScreen.OnePerFrame`의 `perFrame.Enqueue` (`perFrame.Count`로 셈) | `RunOnePerFrame`의 `Dequeue`, 숨김·닫기의 `perFrame.Clear()` (취소) |
| 기록 화면 낮은 우선순위 큐 (타임라인 `QueueTimeline` 포함) | `RunOnePerFrame`에서 넘길 때 `inFlight++` | 넘긴 작업이 돌 때 `inFlight--` (세대가 달라 건너뛰어도), 넘기기 실패 시 바로 `inFlight--` |
| 리포트 종이 면 그리기·지난 면 떼기 | `ReportScreen.Draw`·`ClearPaper`가 프레임에 걸 때 (`drawing`·`clearing`, `FrameHooks`로 셈) | 다 그리거나 다 떼서 프레임에서 뗄 때, 세대가 바뀌어 버릴 때, 화면을 떠나 멈출 때 |
| 쇼케이스 대화상자 절의 열기 예약 | `ShowcaseWindow.ShowSection`에서 넘길 때 `pendingWork++` | 그 작업이 돌 때 `pendingWork--` |
| 배경 결 그리기 (G4 M12 1라운드 5) | `Lights.ScheduleGrain`에서 `grainPending = true` (1로 셈) | 결을 붙였을 때, 그릴 것이 없을 때(같은 크기·아직 배치 전), 결을 껐을 때, 만들기 실패. 만드는 사이 새 예약이 걸리면 그 예약이 끝낸다. 끝나면 `GrainSettled`로 진단을 다시 쓴다 |

셸 값은 `renderQueued + ScreenHost.PendingDraws`(공통 요소 이동 대기 + **지금 화면**의 `Screen.PendingDraws`) + 결 그리기 예약.
셸의 `generation`에는 결 이미지를 붙인 횟수(`Lights.GrainGeneration`)를 더한다.
작업이 끝나 수가 줄 때 진단을 다시 쓰게 했다 (기록 화면 조각 실행, 리포트 떼기 끝. 셸 렌더와 리포트 그리기 끝은 원래 진단을 다시 썼다).

### finiteMotions: 끝나는 모션 수

- 종류(`MotionKind`)가 아니라 유한/무한·고정으로 나눈다. `MotionHandle.Finite = !forever` (`Motion.Run`에서 정함).
  횟수가 정해진 깜빡임(`Blink`, 종류 Repeat)은 센다. 끝없는 반복(`Breathe`·`Sweep`·`RunRepeat`)은 세지 않는다.
- 진행도 고정(`--motion-hold`, `Motion.HoldAt`)으로 애니메이션 제어기를 멈춘 모션은 `MotionHandle.Held = true`로 표시하고 세지 않는다 (`heldMotions`에 따로 나옴).
- 늘어나는 곳: `Motion.Run`의 `active.Add`. 줄어드는 곳: `Finish`의 `active.Remove` (끝남, `Stop`·움직임 줄이기 취소, 소유 요소 이탈).
- 원형 테마 전환(`Theme.SwitchAsync`, Motion 밖)도 끝나는 모션 1로 센다. `Theme.HoldAt`(`--reveal-hold`)으로 멈추면 `Theme.TransitionHeld = true`가 되어 세지 않는다. 전환이 끝나면 거짓으로 돌린다.

### generation / sceneGeneration: 장면 세대

- `generation`: `ScreenHost.Show`(화면 이동)와 `ScreenHost.Render`(셸의 다시 그리기)마다 1 오른다. 쇼케이스는 절 바꾸기마다.
- `sceneGeneration`: 셸을 거치지 않는 화면 안 다시 그리기. 기록 화면 `renderGen`(실제 그리기·숨김마다), 리포트 화면 `drawGen`(종이 면 새로 그리기마다), 나머지 0.
- 두 수를 더하지 않고 따로 낸다 (더하면 화면이 바뀔 때 우연히 같은 합이 될 수 있다).

### laidOut: 필수 요소 배치

- 화면마다 자동화 ID 목록을 따로 두지 않고, 이미 있는 등장 목록 `Screen.Parts`(차례로 등장하는 카드)를 필수 요소로 쓴다.
- 지금 화면이 트리에 붙어(`IsLoaded`) 크기가 있고, 보이는 `Parts`가 모두 `ActualSize > 0`이면 참.
  자기나 조상이 접힌 요소, 화면까지 부모가 이어지지 않는 요소는 판정에서 뺀다.
- 쇼케이스: 지금 절의 카드(`sectionHost.Children`).

### timelineOk: 타임라인 열 수

- 지금 화면의 타임라인(`Screen.SettleTimeline`: 홈·측정·기록 상세)만 본다. `TimelinePanel.FrameMatches`.
- 배치돼 폭이 있고 장면을 가진(`hasFrame`) 타임라인만: `frameColumns == Columns`(= max(1, round(plotWidth))). 시험의 `TimelineProblems`와 같은 판정.
- 타임라인이 없는 화면, 빈 상태, 장면이 없는 타임라인은 참.

### 진단을 다시 쓰는 때

배치·타임라인 조건이 참이 되는 순간을 알려 주는 사건이 없다. 그래서 `laidOut`이나 `timelineOk`가 거짓으로 쓰이면 뿌리 요소의 `LayoutUpdated`를 듣고,
배치가 끝날 때마다 조건만 다시 본다. 참이 되면 진단을 한 번 다시 쓰고 듣기를 끝낸다. 거짓인 동안 진단을 계속 쓰지 않는다 (진단 글이 바뀌면 배치가 다시 일어나 되먹임 고리가 된다).

## 시험: 기다리는 순서 (`AppDriver.WaitSettledAndCapture`)

1. 장면의 기대 상태: 기존 `wait:` 동작(`M5MatrixTests.Open`)과 각 시험의 `WaitDiag`. 호출 전에 끝나 있다.
2. 앱이 알린 "그리기 끝남": 진단 `settle.settled`가 참. 아니면 30 ms마다 다시 읽는다.
3. 창 캡처 두 번이 같음(보조): 참이 된 순간의 `settle` 값(장면 세대·settled·작업 수 전부)을 열쇠로 잡고 캡처 A → 50 ms 뒤 다시 읽어 같은 열쇠인지 → 캡처 B → 다시 읽어 같은 열쇠인지.
   A와 B가 화소(B·G·R)까지 같고 그 사이 열쇠가 그대로면 끝. 아니면 B를 새 A로 삼아 되풀이한다.
- 최대 대기: 700 ms (`Settle.MaxWait`, 지금까지의 고정 대기와 같음). 진행도 고정 전환 장면(`TransitionFrame`, M6·M7 전환, M11 설정·동의 전환)은 지금까지와 같은 500 ms.
  넘으면 그때 한 장을 새로 찍어 쓴다 (기존처럼).
- 순수 판정(`Settle.Read`, `Settle.SameImage`)은 `tests/NetPulse.UiTests/Settle.cs`, 앱 없이 도는 시험은 `SettleTests.cs`.

## 기록하는 것

각 매트릭스 결과 JSON(`g5/matrix-*.json`)과 M5G5 `reducedmotion-home`에 `captureWait`:

```
captureWait = { settled, timedOut, waitedMs, captures, appReported }
```

- `settled`: 1~3을 모두 만족해 찍음. `timedOut`: 최대 대기를 넘겨 찍음. 둘은 따로 기록한다 (둘 다 참일 수는 없다).
- `waitedMs`: 기다린 시간, `captures`: 찍은 장수(비교용 포함), `appReported`: 진단에 `settle`이 있었는가.
- 캡처 이름·경로·나머지 기록은 그대로다. 결과 JSON의 `diag`에도 `settle`이 함께 남는다.

바꾼 곳: M5(기본·추가·전환), M6·M7·M8(기본·G5 추가·전환), M9(기본·추가), M10, M11(기본·설정 전환·동의 전환), M12 매트릭스, G5 캡처 매트릭스(쇼케이스), M5G5 움직임 줄이기 캡처.
바꾸지 않은 곳: 캡처 앞이 아닌 대기(`M5MatrixTests.TransitionFrame`의 첫 실행 등장 대기, `M9GapTests`), 매트릭스가 아닌 시험의 대기.

## 알려진 빈틈

- 사용 조건 창(`TermsWindow`, terms 장면)은 진단에 `settle`이 없다. 늘 최대 대기까지 기다리고 `appReported = false`, `timedOut = true`로 남는다 (지금까지와 같은 시간).
- 세지 않는 예약 작업: 초점·스크롤을 맞추는 낮은 우선순위 작업(`DiagnoseScreen`·`ProfileEditor`·`DiagnosisParts`·`MeasureParts`의 `StartBringIntoView`·초점, `LegalView.BringOpenIntoView`),
  `HomeScreen`·`RecordsScreen`·`ReportScreen`·`SettingsScreen`의 기록 변경 알림으로 큐에 넣는 다시 그리기. 그리기 조각이 아니라 따로 세지 않았다.
  진단 쓰기도 같은 낮은 우선순위로 그 뒤에 들어가는 경우가 많고, 화면이 바뀌면 3(캡처 비교)에서 걸린다.
- 숨은(지금이 아닌) 화면의 예약 작업은 세지 않는다. 화면에 보이지 않기 때문이다.
- 진단은 쓴 순간의 값이다. 쓴 뒤 새 작업이 예약되면 다음 쓰기까지 낡은 "끝남"을 읽을 수 있다. 두 캡처 앞뒤 열쇠 비교와 화소 비교로 줄였지만 완전히 막지는 못한다.
- 측정이 계속 도는 장면(데모 시간을 멈추지 않은 장면)은 셸 렌더마다 `generation`이 올라 열쇠가 계속 바뀐다. 그런 장면은 최대 대기까지 가고 `timedOut`으로 남는다.
- 글 상자 커서 깜빡임처럼 앱이 세지 않는 시스템 그리기가 있으면 두 캡처가 달라 최대 대기까지 간다.
- `laidOut`은 등장 카드 단위다. 카드 안의 개별 요소(목록 줄 등)까지 보지 않는다. 그 안의 늦은 그리기는 예약 작업 수와 캡처 비교에 맡긴다.
- 이 기록은 구현 시점의 것이고, 시험은 아직 돌리지 않았다. 첫 실행에서 M11 캡처와 장면별로 비교한다 (설계).
