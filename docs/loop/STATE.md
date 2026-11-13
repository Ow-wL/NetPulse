# 루프 상태

재개할 때는 이 파일과 `docs/loop-plan.md`를 먼저 읽는다.

## 현재

| 항목 | 값 |
| --- | --- |
| 마일스톤 | M0 결정 |
| 단계 | ⑦ 마감 — D1(C, WinUI 3)·D2(흑요석) 확정, 푸시·PR |
| 브랜치 | `loop/m0-decisions` (base `main` = `e852c6b`) |
| 측정한 소스 | `39c2612` (깨끗한 작업트리), 원본 `.loop/logs/m0-r4/` |
| diff 리뷰 라운드 | 3 / 3 — `APPROVE` |
| 누적 실패 | 2 / 8 (G4 1·2라운드 `REVISE`) |
| PR | 만드는 중 |

## 게이트 (M0 전용 절차, 기획서 8절)

| 게이트 | 상태 | 근거 |
| --- | --- | --- |
| G1 빌드 기록 | 통과 | `spike-results.md` 측정 근거 표 (명령, SDK, 실행 파일 해시) |
| G2 검증·미검증 구분 | 통과 | `spike-report.md` "이 스파이크가 증명하지 못한 것" |
| G3 실제 조작·진단 API | 통과 | `run-spike.ps1`, P6·P8 |
| G5 후보 비교 | 완료 | `spike-results.md`, `spike-report.md`, `design-options.md` |
| G4 Codex | 3라운드 `APPROVE` | `.loop/reviews/m0-g4-r3-codex.md` |

## 미해결 지적

없음. 3라운드의 후속 항목(자원 측정 정밀화, 실제 표시 지연·장시간·IME·배율 검증)은 제품 마일스톤에서 다룬다.

## 다음 할 일

1. M0 마감: 커밋 단위 공개 전 검사 → 푸시 → PR (M0은 CI 적용 안 함)
2. M1 골격 설계안(`docs/loop/M1-design.md`) → Codex 설계 검토
