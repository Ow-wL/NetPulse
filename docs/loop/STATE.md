# 루프 상태

재개할 때는 이 파일과 `docs/loop-plan.md`를 먼저 읽는다.

## 현재

| 항목 | 값 |
| --- | --- |
| 마일스톤 | M4 근거와 분석 엔진 |
| 단계 | ③ 구현 |
| 브랜치 | `loop/m4-analysis` (base `loop/m3-engine`) |
| 설계 검토 라운드 | 2 / 2 (Claude 판정으로 3판 확정) |
| diff 리뷰 라운드 | 0 / 3 |
| 누적 실패 | 4 / 8 |
| PR | 없음 |

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
| M0~M3 통합 | `loop/m2-design-system` | Ow-wL/NetPulse#7 (base `main`) | 열림, 사용자 승인 대기 |

## 미해결 지적 (M2 G4 3라운드, 차단 아님 → M5에서 처리. M3에는 화면이 없어 그대로 둔다)

1. 중요: 창을 닫을 때 진행 중인 원형 전환의 정리 완료를 기다리지 않는다 (`Theme.Detach` 직후 창 닫기). 전환 완료 작업을 보관하고 닫기 전에 기다린다. 실제 종료 오류는 재현되지 않았다.
2. 중요: 대화상자 뒤 범위에 글자·그래프색이 들어 있지 않다. 넣으면 대화상자 위 `muted` 대비가 계산상 약 4.40으로 기준(4.5) 아래다. 대화상자 배후 범위와 실측 장면을 넓히고 재질을 조정한다.
3. 사소: 라이트의 세 재질 두께 구분이 약하다 (가독성 때문에 얇은 유리 불투명도를 올린 결과). 테두리·틴트·그림자로 조정한다.

## 미해결 지적 (M3 G4 3라운드, 차단 아님 → M4에서 처리함, 커밋 `2a1cc89`)

1. 중요: 복귀와 직전 표본의 경과 시간이 같으면(단조 시계 정지, 밀리초 절삭) 복귀 경계를 놓쳐 거짓 `clock` 이벤트가 남는다 (`GapTracker.CheckClock`의 `to > lastClockMs`). 아직 반영하지 않은 복귀를 따로 기억하고 같은 시각 시험을 더한다. 회차·손실 집계에는 영향 없음.
2. 중요: 시험의 쓰기 진입 신호가 "마지막 실패 완료"를 보장하지 않는다 (`RecordFakes` 횟수는 고장 결정 전에 오름). 실패 완료·배출 시도 완료 신호로 단계를 고정한다 (`ReviewR1Tests` 89·120행 부근).
3. 중요: `SuspendGapSurvivesWhenRoundsAfterResumeAreSealedAsUnprocessed`는 원래 경쟁(꺼낸 뒤 분류 잠금 전에 마감)을 재현하지 못한다. 꺼냄과 분류 사이에 끼어드는 시험 지점을 두어 고정한다. 코드는 같은 잠금으로 해결됨.
- 참고: 요약 게시가 시작된 뒤(이름 바꾸기)는 종료가 제한 없이 기다린다. 일관성을 위한 선택이며 "종료 기한은 게시 시작 전까지"로 표현한다.

## 다음 할 일

1. M4 설계 3판대로 구현 (근거 수집 → 판정 → 빠른 진단)
2. 구현 (M3 이월 지적 1~3 포함) → G1·G2, G3(화면 없는 통합 시나리오, 이 PC 실측) → G4 → 마감 (`close-milestone.ps1 -UiOptional`)
