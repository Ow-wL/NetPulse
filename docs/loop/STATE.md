# 루프 상태

재개할 때는 이 파일과 `docs/loop-plan.md`를 먼저 읽는다.

## 현재

| 항목 | 값 |
| --- | --- |
| 마일스톤 | M2 디자인 시스템 |
| 단계 | ① 설계안 작성 |
| 브랜치 | `loop/m2-design-system` (base `loop/m1-skeleton`) |
| 설계 검토 라운드 | 0 / 2 |
| diff 리뷰 라운드 | 0 / 3 |
| 누적 실패 | 2 / 8 |
| PR | 없음 |

## 지난 마일스톤의 게이트 (M1)

| 항목 | 값 |
| --- | --- |
| 승인받은 코드 커밋 X | `bb7ec370b272b8f382e825d6ddcd05c5def0c2a3` |
| G1·G2 | `build/build.ps1 -Gate`: 경고 0, 오류 0. Core 35/35, Platform 3/3, 미실행 0 |
| G3 | `build/ui-test.ps1`: UI 3/3 |
| G4 | `.loop/reviews/m1-g4-r3-answer.md`: `판정: APPROVE` |
| manifest.json SHA-256 | `720A9EDE2A0AAE751636FEBB42948485AD3CDC0E5DB4D5002B16FCC5B010F921` |
| ui-test.json SHA-256 | `14E11FA8EC09CB0294BB7D46EB09A4E01013A23495589A0FCB2BC43FB209BBE3` |

증거 파일은 이 PC의 `artifacts/bb7ec370b2/`에 있다 (공개하지 않음).

## 지난 마일스톤

| 마일스톤 | 브랜치 | PR | 상태 |
| --- | --- | --- | --- |
| M0 결정 | `loop/m0-decisions` | Ow-wL/NetPulse#1 | 열림, 사용자 머지 대기. D1 = WinUI 3, D2 = 흑요석 |
| M1 골격 | `loop/m1-skeleton` | Ow-wL/NetPulse#2 (base M0) | 열림, 사용자 머지 대기. CI 실행 43381315413 성공 (PR head `c4f72af`) |

## 미해결 지적 (M1 G4 3라운드, 차단 아님 → M2 구현에서 처리)

1. 중요: `MeasurementEngine.StopAsync`가 제한시간으로 `false`를 돌려준 뒤 다시 부르면 남은 작업과 무관하게 `true`를 돌려준다. `DisposeAsync`가 취소 콜백·측정 작업이 끝나기 전에 취소 원본을 정리할 수 있다. 취소·측정의 공동 완료 작업을 보관해 재호출도 그것을 기다리고, 정리는 실제 완료 뒤에 한다.
2. 사소: `PlatformTests.EngineRunsWithRealProbeOnLoopback`이 세 회차를 모두 건너뛴 경우(`Latest` 없음)를 가정하지 않는다. `Rounds > 0`일 때만 마지막 회차를 검사한다.

## 다음 할 일

1. `docs/loop/M2-design.md` 작성 → Codex 설계 검토
2. 구현 (미해결 지적 2건 포함) → G1~G3, G5 → G4 → 마감
