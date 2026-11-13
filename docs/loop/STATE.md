# 루프 상태

재개할 때는 이 파일과 `docs/loop-plan.md`를 먼저 읽는다.

## 현재

| 항목 | 값 |
| --- | --- |
| 마일스톤 | M5 홈과 측정 화면 |
| 단계 | 설계 |
| 브랜치 | `loop/m5-home-measure` (base `main` `744aef2`) |
| 설계 검토 라운드 | 0 / 2 |
| diff 리뷰 라운드 | 0 / 3 |
| 누적 실패 | 0 / 8 |
| PR | 없음 |

**저장소 이전 (2026-11-14, `DECISIONS.md` "공개 저장소 이전")**: 공개 저장소 `Ow-wL/NetPulse`는 이력을 다시 쓴 새 저장소다. 아래 M0~M4 기록의 SHA·PR 번호는 옛 저장소(`Ow-wL/NetPulse-private`, 비공개) 기준이다.

## 지난 마일스톤의 게이트 (M4)

| 항목 | 값 |
| --- | --- |
| 승인받은 코드 커밋 X | `e63658faa67f497172da0910b59f7e7a90827b8d` (코드 끝 `6a2cc21` + 결정 기록) |
| G1·G2 | `build/build.ps1 -Gate`: Core 258/258, Design 109/109, Platform 29/29, 미실행 0. `SessionLinkTests` 디버그 5회 반복 통과 |
| G3 | 화면 없는 통합 (Platform 시험 안): 인터페이스 직접 관측 오프셋·읽기 비용, 실제 세션 링크 수집, 링크 로그 읽기, 빠른 진단, 데모 `link-drop.json`. 이 PC 실측 기록 `docs/loop/m4/pc-measurement.md` |
| G4 | `.loop/reviews/m4-g4-r6-answer.md`: `판정: APPROVE` (차단 0, 사용자 예외 6라운드) |
| manifest.json SHA-256 | `BFE22A5EE643B785527A83CEFA5B1D9150FFFFB9613EB421797DC56639F56D5B` |

## 지난 마일스톤의 게이트 (M3)

| 항목 | 값 |
| --- | --- |
| 승인받은 코드 커밋 X | `f69ff61bde49b6778f1ceab7b1cc81ebbed3b449` |
| G1·G2 | `build/build.ps1 -Gate`: 경고 0, 오류 0. Core 132/132, Design 109/109, Platform 10/10, 미실행 0 |
| G3 | 화면 없는 통합 시나리오 (Platform 시험 안): 실제 시계·ICMP·경로·파일로 측정 → 중지 → 다시 읽기 일치, 데모 `outage.json` 실제 파일 왕복·잘림 복구, TCP 루프백, 이 PC 탐지, flush 비용 p95 0.45 ms (기준 50 ms). 배포본 실시간 모드 6초 실행: 탐지 Ok → 세션 7회차 관측·확정 |
| G4 | `.loop/reviews/m3-g4-r3-answer.md`: `판정: APPROVE` (차단 0) |
| manifest.json SHA-256 | `9FE42636274C0DDD41F31161F27BBD8054ECDC30FF12695F893C9716E2925110` |

증거 파일은 이 PC의 `artifacts/f69ff61bde/`에 있다 (공개하지 않음). UI 시험은 돌리지 않았다 (M3은 화면 없음, `-UiOptional`). `ShellTests`의 실시간 모드 단언은 바꿨으므로 M4·M5의 UI 시험에서 함께 확인한다.

## 지난 마일스톤의 게이트 (M2)

| 항목 | 값 |
| --- | --- |
| 승인받은 코드 커밋 X | `131644c94350bdfd89957884a8777fd80e7b191c` |
| G4 | `.loop/reviews/m2-g4-r3-answer.md`: `판정: APPROVE` |
| manifest.json SHA-256 | `899278096DBB490EBE3E1CEF7369B211BE1DB690C42B3EF810427F0FE0951F2B` |

## 지난 마일스톤

| 마일스톤 | 브랜치 | PR | 상태 |
| --- | --- | --- | --- |
| M0 결정 | `loop/m0-decisions` | Ow-wL/NetPulse#1 | 열림, 사용자 머지 대기. D1 = WinUI 3, D2 = 흑요석 |
| M1 골격 | `loop/m1-skeleton` | Ow-wL/NetPulse#2 (base M0) | 열림, 사용자 머지 대기. CI 성공 |
| M2 디자인 시스템 | `loop/m2-design-system` | Ow-wL/NetPulse#3 (base M1) | 열림, 사용자 머지 대기. CI 실행 43415674372 성공 (PR head `085f382`) |
| M3 측정 엔진 | `loop/m3-engine` | Ow-wL/NetPulse#4 (base M2) | 병합됨 (`loop/m2-design-system`) |
| M0~M3 통합 | `loop/m2-design-system` | Ow-wL/NetPulse#7 (base `main`) | `main`에 병합됨 (병합 커밋 `2af5dfe`) |
| M4 근거와 분석 엔진 | `loop/m4-analysis` | Ow-wL/NetPulse#8 (base `main`) | `main`에 병합됨. 이후 새 공개 저장소로 이전 (새 `main` `744aef2`) |

(이 표의 PR·SHA는 옛 저장소 기준. M5부터 새 공개 저장소.)

## 미해결 지적 (M2 G4 3라운드, 차단 아님 → M5에서 처리. M3에는 화면이 없어 그대로 둔다)

1. 중요: 창을 닫을 때 진행 중인 원형 전환의 정리 완료를 기다리지 않는다 (`Theme.Detach` 직후 창 닫기). 전환 완료 작업을 보관하고 닫기 전에 기다린다. 실제 종료 오류는 재현되지 않았다.
2. 중요: 대화상자 뒤 범위에 글자·그래프색이 들어 있지 않다. 넣으면 대화상자 위 `muted` 대비가 계산상 약 4.40으로 기준(4.5) 아래다. 대화상자 배후 범위와 실측 장면을 넓히고 재질을 조정한다.
3. 사소: 라이트의 세 재질 두께 구분이 약하다 (가독성 때문에 얇은 유리 불투명도를 올린 결과). 테두리·틴트·그림자로 조정한다.

## 미해결 지적 (M3 G4 3라운드, 차단 아님 → M4에서 처리함, 커밋 `2a1cc89`)

1. 중요: 복귀와 직전 표본의 경과 시간이 같으면(단조 시계 정지, 밀리초 절삭) 복귀 경계를 놓쳐 거짓 `clock` 이벤트가 남는다 (`GapTracker.CheckClock`의 `to > lastClockMs`). 아직 반영하지 않은 복귀를 따로 기억하고 같은 시각 시험을 더한다. 회차·손실 집계에는 영향 없음.
2. 중요: 시험의 쓰기 진입 신호가 "마지막 실패 완료"를 보장하지 않는다 (`RecordFakes` 횟수는 고장 결정 전에 오름). 실패 완료·배출 시도 완료 신호로 단계를 고정한다 (`ReviewR1Tests` 89·120행 부근).
3. 중요: `SuspendGapSurvivesWhenRoundsAfterResumeAreSealedAsUnprocessed`는 원래 경쟁(꺼낸 뒤 분류 잠금 전에 마감)을 재현하지 못한다. 꺼냄과 분류 사이에 끼어드는 시험 지점을 두어 고정한다. 코드는 같은 잠금으로 해결됨.
- 참고: 요약 게시가 시작된 뒤(이름 바꾸기)는 종료가 제한 없이 기다린다. 일관성을 위한 선택이며 "종료 기한은 게시 시작 전까지"로 표현한다.

## 미해결 지적 (M4 G4 5·6라운드, 차단 아님 → M5에서 처리)

1. 중요: 링크 표본 마감 검사의 두 위치(받을 때, 큐에서 꺼낼 때)를 각각 고정해 시험한다 (`SessionLinkTests`).
2. 중요: 넘침 경쟁 시험(`LateOverflowRegistrationCannotMergeDownsAcrossTheLostSample`)이 큐 배출·재관측 생산자 진입을 `Task.Delay(200)`에 기댄다. 신호로 확인하고, 분리된 두 다운 구간의 존재·경계를 단언한다.
3. 중요 (새 공개 저장소 첫 CI에서 발견): `OverflowIsRecorded`가 CI에서 한 번 시간 초과로 실패했다 (회차마다 알림 3000개를 넣고 "관측" 조건을 기다림). 같은 코드가 옛 저장소 PR CI와 로컬 15회 반복에서는 통과했고, 재실행에서 통과. 느린 실행기에서 시간에 민감한 시험이다. 시험을 신호 기반으로 바꾸거나, 넘침 중 처리 지연이 제품 동작 문제인지 먼저 확인한다.

## 다음 할 일

M5 설계안(`docs/loop/M5-design.md`)을 쓰고 Codex 설계 검토를 받는다. 위 미해결 지적(M2 3건, M4 3건)을 M5에서 함께 처리한다.
- 브랜치는 새 `main`에서 딴다. 옛 이력 브랜치(`loop/m0`~`m4`, 원격 `old-private`)를 `origin`에 푸시하지 않는다.
