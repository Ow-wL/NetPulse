# 루프 상태

재개할 때는 이 파일과 `docs/loop-plan.md`를 먼저 읽는다.

## 현재

| 항목 | 값 |
| --- | --- |
| 마일스톤 | M2 디자인 시스템 |
| 단계 | ⑦ 마감 (푸시, PR, CI 확인) |
| 브랜치 | `loop/m2-design-system` (base `loop/m1-skeleton`) |
| 설계 검토 라운드 | 2 / 2 — 2라운드 `REVISE`(차단 0), 남은 6건 Claude 판정으로 수용 |
| diff 리뷰 라운드 | 3 / 3 — 1·2라운드 `REVISE`, 3라운드 `APPROVE` |
| 누적 실패 | 4 / 8 (M1 G4 `REVISE` 2회, M2 G4 `REVISE` 2회) |
| PR | 마감 스크립트가 만든다. 번호는 M3 첫 커밋에서 적는다 |

## 게이트 통과 (M2)

| 항목 | 값 |
| --- | --- |
| 승인받은 코드 커밋 X | `131644c94350bdfd89957884a8777fd80e7b191c` |
| G1·G2 | `build/build.ps1 -Gate`: 경고 0, 오류 0. Core 36/36, Design 109/109, Platform 3/3, 미실행 0 |
| G3 | `build/ui-test.ps1`: UI 96/96 (셸, 전시 화면, 키보드, G5) |
| G5 | `g5.json` passed=true. 캡처 매트릭스 44장. 벤치 요약 `docs/loop/m2/bench.md` |
| G4 | `.loop/reviews/m2-g4-r3-answer.md`: `판정: APPROVE` |
| manifest.json SHA-256 | `899278096DBB490EBE3E1CEF7369B211BE1DB690C42B3EF810427F0FE0951F2B` |
| ui-test.json SHA-256 | `6ABB36632B7D9ECF2E9C8C0B8B8ACFC53F9E3AD3E8A7594C5B88CA0572C20DF9` |
| g5.json SHA-256 | `95FFF63FFDE29C48E6500E458BA6EE9AA5107D1A5968EEF46BF662114C0A0361` |

증거 파일은 이 PC의 `artifacts/131644c943/`에 있다 (공개하지 않음).
키보드 시험은 사용자가 PC를 비우고 바탕화면이 앞에 있을 때만 돈다 (전경 확인 뒤에만 키 전송). 원격 접속(RustDesk) 중에는 프레임 측정이 흔들렸다.

## 지난 마일스톤

| 마일스톤 | 브랜치 | PR | 상태 |
| --- | --- | --- | --- |
| M0 결정 | `loop/m0-decisions` | Ow-wL/NetPulse#1 | 열림, 사용자 머지 대기. D1 = WinUI 3, D2 = 흑요석 |
| M1 골격 | `loop/m1-skeleton` | Ow-wL/NetPulse#2 (base M0) | 열림, 사용자 머지 대기. CI 성공 |

## 미해결 지적 (M2 G4 3라운드, 차단 아님 → M5에서 처리)

1. 중요: 창을 닫을 때 진행 중인 원형 전환의 정리 완료를 기다리지 않는다 (`Theme.Detach` 직후 창 닫기). 전환 완료 작업을 보관하고 닫기 전에 기다린다. 실제 종료 오류는 재현되지 않았다.
2. 중요: 대화상자 뒤 범위에 글자·그래프색이 들어 있지 않다. 넣으면 대화상자 위 `muted` 대비가 계산상 약 4.40으로 기준(4.5) 아래다. 대화상자 배후 범위와 실측 장면을 넓히고 재질을 조정한다.
3. 사소: 라이트의 세 재질 두께 구분이 약하다 (가독성 때문에 얇은 유리 불투명도를 올린 결과). 테두리·틴트·그림자로 조정한다.

## 다음 할 일

1. `build/close-milestone.ps1 -Screens`로 M2 마감 (푸시, PR, CI 확인)
2. M3 측정 엔진: 첫 커밋에서 M2 PR 번호·CI 결과를 적는다
