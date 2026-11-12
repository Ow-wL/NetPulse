# 루프 상태

재개할 때는 이 파일과 `docs/loop-plan.md`를 먼저 읽는다.

## 현재

| 항목 | 값 |
| --- | --- |
| 마일스톤 | M0 결정 |
| 단계 | ⑥ G4 리뷰 2라운드 |
| 브랜치 | `loop/m0-decisions` (base `main` = `e852c6b`) |
| 측정한 소스 | `9540198` (깨끗한 작업트리) |
| diff 리뷰 라운드 | 2 / 3 |
| 누적 실패 | 1 / 8 (G4 1라운드 `REVISE`) |
| PR | 없음 |

## 게이트 (M0 전용 절차, 기획서 8절)

| 게이트 | 상태 | 근거 |
| --- | --- | --- |
| G1 빌드 기록 | 통과 | `spike-results.md` 측정 근거 표 (명령, SDK, 실행 파일 해시) |
| G2 검증·미검증 구분 | 통과 | `spike-report.md` "이 스파이크가 증명하지 못한 것" |
| G3 실제 조작·진단 API | 통과 | `run-spike.ps1`, P6·P8 |
| G5 후보 비교 | 완료 | `spike-results.md`, `spike-report.md`, `design-options.md` |
| G4 Codex | 1라운드 `REVISE` → 수정 → 2라운드 대기 | `.loop/reviews/m0-g4-r1-codex.md` |

## 미해결 지적

없음 (1라운드 13건 모두 처리, `DECISIONS.md` 참조)

## 다음 할 일

1. Codex G4 2라운드
2. D1·D2 사용자 컨펌
3. D1이 C면 고른 시안의 대표 요소를 C로 재현해 확인 (spike-report "확정 전 추가 확인")
4. M0 마감: 공개 전 검사 → 푸시 → PR
