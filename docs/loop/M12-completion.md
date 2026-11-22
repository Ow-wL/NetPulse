# M12 완성 정의 점검

M12 설계 A~H (`docs/loop/M12-design.md` 3판 + G2 절 수정). 설계 H대로 `M11-completion.md`는 고치지 않는다. M11 증거를 참조하고, M12에서 바뀐 수용 기준·새 증거·남은 한계만 여기에 적는다.

> **지금 상태: 초안 (2026-11-23).** 승인 코드 X는 아직 없다 (G4 진행 중: 1·2라운드 REVISE, 고침 반영). 마지막 칸은 같은 X에서 노트북 최종 게이트(low 프로필, 성능 시험 3회)와 증거 검토를 마친 뒤 채운다. 시험 장소는 사용자 결정으로 노트북이다 (DECISIONS "M12 시험 장소: 노트북").

상태 표기는 M11과 같다 (**증명됨**, **미검증**, **한계**, **결정**).

## 1. 목표별 점검

| 목표 | 구현 | 증거 (시험·장면·기록) | 최종 (X) |
| --- | --- | --- | --- |
| **A** 측정 끝 타임라인 선 빠짐 | `06866e2` 재현 시험 → `9751154` 고침: 배치 뒤 실제 폭으로 열 수를 다시 계산, 폭 0에서는 프레임을 만들지 않음 | `M12TimelineTests.FinishedMeasurementReceivedBeforeLayoutIsRecomputedForTheRealWidth` (`--test-finish-before-layout`). 고침 전 커밋에서 실패하는지 비교 실행은 아직 안 함 | 최종 게이트 뒤 |
| **B** 금융 보안 프로그램 충돌 | B2 시작 단계 기록 `862f578` (`Program.Main`, `startup-trace.jsonl`), 손상 줄 방어 `d68a568` | `M12StartTraceTests` 7개, `M12StartTraceUiTests.NormalRunIsTracedToExitAndAKilledRunIsReportedByTheNextRun`. 재현 실패 기록 (DECISIONS "M12 금융 보안 충돌") | **결정**: 1.0.0 조건에서 뺌 (재현 못 함, README 알려진 문제). 기록 기능은 최종 게이트 뒤 |
| **C** N11 Wi‑Fi 신호 | 읽기 `WlanSignalReader`(위치 권한·SSID 없는 호출만), 기록 `MeasurementSession.Signal`, 근거 `SignalEvidence`, 표시 `SignalTimeline`·리포트 | 단위: `M12WlanSignalTests` 7개, `M12SignalTests`(통계·기록·복원·시나리오), `M12SignalDisplayTests` 11개, `M12SignalColorTests` 4개. 장면 `m12-scenes.txt` 6개(`measure-wifi-*`, `records-detail-wifi`, `report-preview-wifi*`). 노트북 실제 Wi‑Fi 확인 | 최종 게이트 뒤 / 실제 Wi‑Fi는 **미검증** → 노트북 실측 |
| **D** 문구 중간·낮음, C3 표시 결함 | `b18c3aa` 문구 56건 중 55건(유지 1건), `86bdfd1` 표시 결함 | `docs/loop/m12/wording-applied.md`(항목별 처리·바뀐 시험), `docs/loop/m12/display-fixes.md`, `M12DisplayTests` 3개. 옛 용어 잔여 검색(2026-11-23): 화면·리포트·README에 없음 | 최종 게이트 뒤 |
| **E** 흑요석 미세 결, 투명 효과 끔 색 | `43b185e`: 시안 CSS 점 격자, Acrylic일 때만. 불투명 표면은 같은 휘도의 무채색 | `M12GrainTests` 7개, 대체 모드 매트릭스 장면 | 최종 게이트 뒤 + 시각 검토 |
| **F** 단추·칩·배너·카드 본문 대비 | `0c9e3fe`: 숨김 고리, 비활성 단추 토큰과 점선 테두리 | `M12ContrastTests.PartsAndCardBodyTextAreReadableOnTheScreen`(6장면 × 다크·라이트), `DisabledButtonContrastTests` 4개, `docs/loop/m12/contrast-parts.md` | 최종 게이트 뒤 / **결정**: 비활성 단추 3:1 이상 (사용자 결정 "예외로 두기") |
| **G1** 게이트 시간 | C 운영(노트북 확인), B 상태 기반 캡처 대기(`settle`, `WaitSettledAndCapture`), A 병렬은 하지 않음 | `SettleTests` 4개, `docs/loop/m12/capture-wait.md`. 단축 전후 시간은 같은 PC 실측으로만 말한다 (노트북 기준 비교는 아래 3절) | 최종 게이트 뒤 |
| **G2** 프레임 기준 | `ae6190d`: 주사율 보정, `standard`·`low` 프로필 (계약 `m12-g2-1`), 고지 측정 앞뒤 주사율 비교 `d68a568` | `M12FrameContractTests` 6개, 성능 시험 5개의 g5 json(계약·프로필·Hz·standard 판정) | 노트북 `low` 3회 모두 통과해야 함 |
| **G3** 시간 민감 시험 | `ced0119`: `Rounds`가 회차마다 관측을 기다림 | CI 단위 시험 | 최종 CI 뒤 |
| **H** 1.0.0 | `0c7d10a`: `Directory.Build.props` 1.0.0, README 기능(Wi‑Fi 신호)·알려진 문제 | `M10AboutTests.AboutCardShowsVersionBuildAndLicense`, `M10ReleaseExeTests`(1.0.0 배포 파일) | 최종 게이트 뒤, 게시는 사용자 승인 뒤 |

## 2. M12에서 바뀐 기능의 상태

| 기능 | M11 최종 | M12에서 바뀐 것 | M12 최종 |
| --- | --- | --- | --- |
| R2 링크 근거 | 유선 증명됨, Wi‑Fi 해석 증명됨, Wi‑Fi 하드웨어 미검증(인수 8) | 링크 별칭을 신호와 공유(`AliasFor`) | 최종 게이트 뒤 |
| N11 Wi‑Fi 신호 (새로 포함) | 범위 밖 | 1절 C | 최종 게이트 뒤 / 실제 Wi‑Fi 미검증 |
| N1 겹침 타임라인 | 증명됨 | 신호 오른쪽 축, 측정 끝 선 빠짐 고침(A) | 최종 게이트 뒤 |
| N6 리포트 | 증명됨 | 신호 근거 표·그림, 용어 | 최종 게이트 뒤 |
| 문구 전반 | 높음 11건 | 중간·낮음 55건 | 최종 게이트 뒤 |

## 3. 노트북 확인 기록

| 차수 | 커밋 | 결과 | 이어진 고침 |
| --- | --- | --- | --- |
| 1차 | `0c7d10a` | 빌드 게이트 단위 시험 4건 실패 | `4d5474d` |
| 2차 | `4d5474d` | 단위 시험 통과, UI 885 통과·7 실패에서 90분 제한으로 끊김 | `daf08d5` |
| 3차 | `daf08d5` (성능 3회) | 단위 시험 통과, UI 945/946 (사이트 점검 시험 고리 1건, 성능 반복은 실패 때문에 안 돎). report-worst 70.0 ms | 진단 다시 쓰기 (DECISIONS "노트북 중간 확인 3차") |
| 최종 | X (성능 3회) | | |

자세한 원인은 DECISIONS "M12 노트북 중간 확인 1·2차".

## 4. 남은 한계와 이월

- 금융 보안 충돌: 재현하지 못해 원인 미확인 (결정). 오류 보고가 오면 다시 본다.
- 결론 문장(끊김 수)과 등급 `outage-many`(하위 구간 수)의 단위 차이 (G4 1라운드 사소, 기록만).
- 비활성 표현이 `GlassButton`과 `SettingToggle`·`RowButton`·`SegmentedChoice` 사이에 다름 (구현 중 결정 (3)).
- 노트북 한 대의 `low` 통과는 "명시한 노트북에서 계약을 통과했다"이지 대부분의 PC를 대표한다는 증명이 아니다 (설계 G2).
- README 캡처 다시 찍기 (화면을 쓰는 일, 사용자 허락 뒤).
