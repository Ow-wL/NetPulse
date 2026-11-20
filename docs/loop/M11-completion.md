# M11 완성 정의 점검

M11 설계 A·C·D·E·G (`docs/loop/M11-design.md` 3판). 기획서 1절 "루프 완료" 1~6을 하나씩 점검한다.

> **지금 상태: 뼈대 (M11 구현 전, 2026-11-21).** 1절 표는 M11 시작 때 조사(읽기 전용: 기획서·DECISIONS·STATE·설계서·시험 목록 대조)로 채웠다. "증거"는 시험이 **있는지**를 적은 것이고 최종 게이트 통과 여부는 아직 채우지 않았다. B의 새 시험·최종 게이트·증거 검토 뒤에 상태 칸을 확정한다. 시험 이름은 grep으로 존재를 확인했다.

상태 표기 (설계 A):
- **증명됨**: 수용 기준의 각 부분에 대응하는 단언이 있는 시험이 최종 게이트에서 통과했다. 실제 네트워크 성공 여부는 판정하지 않는다(기획서 4절).
- **미검증**: 기준의 일부에 증거가 없다.
- **한계**: 기획서·설계가 미리 인정한 환경 한계. 인수 항목 번호(`acceptance-checklist.md`)를 적는다.
- **결정**: 사용자 결정으로 범위가 달라졌다.

## 1. 필수 기능 23개와 N10

| 기능 | 수용 기준 (기획서 6절 요약) | 구현 | 증거 (지금 있는 시험·장면) | M11 전 판정 | M11에서 |
| --- | --- | --- | --- | --- | --- |
| F1 빠른 진단 | 어댑터·게이트웨이·외부 연결·DNS·HTTPS·네트워크 로그인·링크 상태, 결론 문장, 측정하지 못한 것을 정상으로 보지 않음 | M4·M6 | `DiagnosisTests`(PairCombinationTable, ConclusionRulesInOrder, AllTimeoutsAreInsufficientNotNormal), `M4IntegrationTests.QuickDiagnosisFinishesWithinLimitsAndDefinedStates`, `M6FlowTests.EachConclusionSceneShowsTheExpectedConclusionJudgmentsAndScope`, 장면 `diagnose-*` | 증거 있음 | 최종 게이트 |
| F2 사이트 점검 | 도메인 검증, DNS·HTTPS 한 줄씩 | M4·M6 | `DiagnosisTests.SiteCheck*`, `M6FlowTests.SiteCheckRejectsBadInputAndShowsOkFailAndTimeout`, 장면 `diagnose-site-*` | 증거 있음 | 최종 게이트 |
| F3 실시간 측정 | 여러 대상 동시, 손실·지연 집계, 기록 저장 | M3·M5 | `MeasurementEngineTests`, `SessionTests`, `StatsTests`, `M3IntegrationTests.RealClockIcmpRoutesAndFilesSurviveStopAndReread`, `M5FlowTests.FirstRunToStopShowsWhatTheRecordSays` | 증거 있음 | 최종 게이트, 실측 D3 |
| F4 증상 표시 | 누른 시각을 기록, 다시 읽힘 | M5·M9 | `M5SessionTests`, `KeyboardTests.KeyboardAloneStartsMarksWithAMemoAndStops`, `M9ResidentTests.TrayMenuMarksSymptomAndExitSaves` | 증거 있음 | 최종 게이트 |
| F5 끊김 분석 | 원인 구간 후보 (단정하지 않음) | M4·M7 | `AnalysisTests`(예: ExternalOutageWithRouterUpPointsAfterRouter), `M7FlowTests.AFinishedMeasurementReopensWithTheSameValuesBeforeAndAfterRestart` | 증거 있음 | 최종 게이트 |
| F6 결과 비교 | 두 측정 비교 (대상 대응, 비교 조건, 화살표 규칙) | M7 | `M7ComparisonTests`, `M7FlowTests.ComparisonMatchesCoreAndMismatchedConditionsSuppressArrows` | 증거 있음 | 최종 게이트 |
| F7 보조 | DNS 캐시 비우기(확인 후), **Windows 설정 열기**, 결과 폴더 열기 | M6 | 비우기 `M6FlowTests.Flush*`, 폴더 `RecordFolderOpensTheMeasurementSegmentWhileMeasuringOtherwiseTheRoot`. 설정 열기는 단추가 있는지만 (`M6FlowTests.cs:124`) | **미검증 (설정 열기)** | B1: 단추를 눌러 데모 호출 기록(`settings:`) 단언 |
| R1 대상 탐지·설정 | 첫 실행에 게이트웨이를 찾아 구성 제안, 대상 추가·수정·삭제·순서, 측정 프로필 | M3·M5 | `ProfileDetectionTests`, `M5FlowTests.EditorBlocksInvalidChangesAndIsReadOnlyWhileMeasuring`, FirstRunToStop(이름·시간 수정). 대상 삭제는 취소로 되돌리는 시험만 | **미검증 (추가·삭제·순서·사용·역할, 프로필 추가·삭제·선택, 다시 탐지의 저장·재시작·기록 머리)** | B2 |
| R2 어댑터 범용 링크 근거 | 드라이버에 의존하지 않음, 유선과 Wi‑Fi, 상태 네 가지(미지원·접근 거부·이벤트 없음·끊김 관측)를 정상으로 보지 않음 | M4 | 유선: `EvidenceTests`, `SessionLinkTests`, `M4IntegrationTests.RealSessionKeepsLinkEvidenceThroughFiles`, `docs/loop/m4/pc-measurement.md`. Wi‑Fi 해석 시험 없음 | 유선 증거 있음 / **미검증 (Wi‑Fi 해석)** / **미검증 (Wi‑Fi 하드웨어)** | B3 (해석, 설계 "B3 계약"), 하드웨어는 인수 8 |
| R3 리포트 | N6과 같음 | M8 | N6 | 증거 있음 | 최종 게이트 |
| R4 테스트·CI·버전·배포·README | | M1·M10 | CI(`.github/workflows/ci.yml`, UI 시험 제외), `M10AboutTests.AboutCardShowsVersionBuildAndLicense`, `tools/loop/clean-clone.ps1`, README | 증거 있음 | 최종 X에서 CI·깨끗한 클론 |
| R5 범용 사용 환경 | 저장 위치 대체, 손상 기록, 절전 복귀, 150%, 키보드, IPv4 경로 없음 안내 | M2·M3·M9 | `UnwritableFolderFallsBack`, `RecordMoverTests`, `HiddenSleepGapIsShownOnReopenWithoutADropoutBalloon`, `KeyboardTests`, `RoundTripWorksWhenTheFirstRunFindsNoIpv4Route`, 매트릭스 1.5 | 증거 있음 / **한계 (150%는 `--ui-scale` 시뮬레이션)** | 최종 게이트, 인수 4 |
| N1 겹침 타임라인 | 확대·이동, 손실 띠, 증상, 링크 마커 | M5·M7 | `M5ModelTests`, `M5FlowTests.TimelineZoomsPansAndReturnsToNow`, `KeyboardTests.ClickingAnOutageOnTheTimelineOpensThatEpisodeRow`, 장면 `measure-linkdrop` | 증거 있음 | 최종 게이트 |
| N2 등급 | "관측 부족"·"판단 불가" 포함, 근거 없이 좋은 등급 없음 | M4 | `AnalysisTests`(CleanMeasurementIsGood, MostlyErrorsIsInsufficientNotGood, UndecidableWhenHoldsDominate, ManySkippedRoundsAreLowCoverage) | 증거 있음 | 최종 게이트 |
| N3 끊김 목록·통계 | | M4·M7 | `AnalysisTests.EpisodesBreakOnGapAndHaveOpenEnds`, `M7FlowTests.ListRowsMatchTheCatalog…` | 증거 있음 | 최종 게이트 |
| N4 오진 줄이기 | | M4 | `AnalysisTests`(NearFailsFarSucceedsIsOnlyTargetFailure, OneExternalDownIsNotInternetOutage, NotDiverseExternalsCannotProveOutage, TargetWithoutBaselineIsExcludedButShown) | 증거 있음 | 최종 게이트 |
| N5 안내형 진단 | | M4·M6 | `M6DiagnosisTests.EveryKnownCodeHasPlainWordsAndHeadsAreDistinct`, `M6FlowTests.EachConclusionScene…` | 증거 있음 | 최종 게이트 |
| N6 리포트 | 파일 하나, 가리기, 입력은 글자로만 | M8 | `M8RedactionTests`, `M8HtmlTests`, `M8FlowTests.AFinishedMeasurementBecomesAReportWhoseValuesMatchTheRecord`, `M8G5Tests.ExportedHtmlRendersInEdgeOnScreenAndOnA4` | 증거 있음 / **결정 (가린 리포트에서 메모 뺌)** | 최종 게이트, 인수 9 |
| N7 트레이·알림 | 닫기와 종료 구분, 다시 열기, 같은 끊김에 알림 반복 없음 | M9 | `M9ResidentNotifierTests`, `M9ResidentTests` | 증거 있음 (데모) / **한계 (실제 작업 표시줄 메뉴 [종료]를 UIA로 고르지 못함, 풍선은 사용자 눈 확인)** | 최종 게이트, 실측 D3, 인수 7 |
| N8 메모 | | M5·M7 | `M7MemoTests`, `M7FlowTests.EditedMemoSurvivesReopenAndRestartAndEmptyClears` | 증거 있음 | 최종 게이트 |
| N9 연속 실패 지표 | | M3·M5 | `StatsTests.Runs*`, `M5ModelTests.CardsUseTheStatisticsAndPlainSentences` | 증거 있음 | 최종 게이트 |
| N13 경로 맵 | 확인된 연결과 확인되지 않은 연결을 다르게 그림 | M5 | `M5ModelTests.MapNodesAreObservationsNamedByTheUser` 등, 그림 차이는 캡처 | 증거 있음 | 최종 게이트, 시각 검토 |
| N14 첫 실행 안내 | 세 단계 안에 첫 측정 (M10에서 동의 포함으로 정함) | M5·M10 | `M5FlowTests.FirstMeasurementStartsInOneAction`(동의 미리 씀), `M10TermsTests.TermsComeFirstAndCommandLineRunsOnceAfterConsent`(`--start`) | **미검증 (동의 포함 단계 수)** | B4 |
| N10 TCP (선택, 포함) | | M3·M9 | `M3IntegrationTests.TcpProbeOnLoopback`, `M9SettingsTests.TcpTargetIsSavedMeasuredAndShown`, `KeyboardTests.KeyboardAloneSwitchesATargetToTcp` | 증거 있음 | 최종 게이트 |
| N11 Wi‑Fi 신호 세기 (선택) | | — | — | **결정**: M9에서 뺌 → M12로 (DECISIONS "M10 마감, 0.9.0 게시, M12 범위") | — |
| N12 상시 측정·기록 정리 (선택) | | — | — | **결정**: 계속 뺌 | — |

## 2. 디자인 (완성 조건 2)

- 화면·상태 × 대체 모드 표: 아래 "장면 포함표" (설계 C1). M11 구현 첫 단계에서 채운다.
- 검사 대상 구분 (설계 C3): 배치 검사 대상 / 대비 실측 대상 / 원본 시각 검토 대상 — 최종 게이트 뒤 채운다.
- 미뤄 둔 재검토 3건 (설계 C4): 아래 "재검토" 절.

### 장면 포함표

설계 C1. 2026-11-21에 기존 장면 목록(`m5`~`m10-scenes.txt`)과 추가 장면(`M5MatrixTests`·`M6G5Tests`·`M7G5Tests`·`M8G5Tests`·`M9MatrixTests`·`M10MatrixTests`의 `Extras`)을 읽어 만들었다. 기본 장면은 모두 다크·라이트 × 100·150%로 찍힌다. 아래는 대체 모드만이다.

표기: `있음(장면)` = 지금 찍힘, **추가** = M11에서 더함, 없음(이유) = 일부러 넣지 않음. 대체 모드: HC-A 고대비 aquatic, HC-D 고대비 desert, TO 투명 효과 끔(다크·라이트 중 하나 이상), RM 움직임 줄이기, HC+150 고대비 aquatic + 배율 150%.

| 화면·창 | 대표 상태 (고른 이유) | HC-A | HC-D | TO | RM | HC+150 |
| --- | --- | --- | --- | --- | --- | --- |
| 첫 실행 | `firstrun-detected` (탐지 결과 카드·대상 목록·주요 단추가 함께 보임). RM은 `firstrun-detecting`(탐지 중 진행 표시) | **추가** | **추가** | **추가** (다크·라이트) | **추가** (다크·라이트) | **추가** |
| 첫 실행 IPv4 경로 없음 | `firstrun-noipv4` (새 기본 장면, 데모 `no-ipv4`) | — | — | — | — | — (기본 장면 다크·라이트 × 100·150 **추가**) |
| 홈 | `home-outage` (끊김 배너·카드·경로 맵·그래프). RM은 `home-measuring`(경로 맵 흐르는 빛) | 있음 | 있음 | 있음 (다크·라이트) | **추가** (다크·라이트) | **추가** |
| 측정 | `measure-outage` (띠·마커·카드), RM `measure-live` | 있음 | 있음 | **추가** (다크·라이트) | 있음 (다크·라이트) | **추가** |
| 프로필 편집기 | `profile-ok` (목록·입력 칸·단추·역할 선택) | **추가** | **추가** | **추가** (다크·라이트) | 없음 (편집기에 반복 움직임 없음, 구현 때 확인) | **추가** |
| 진단 | `diagnose-dns` (결론 카드·단계 목록·조치 단추), RM `diagnose-running` | 있음 | 있음 | 있음 (다크·라이트) | 있음 (다크·라이트) | **추가** |
| 기록 목록 | `records-list-problems` | 있음 | 있음 | **추가** (다크·라이트) | 없음 (목록에 반복 움직임 없음, 상세의 RM으로 대표) | 상세로 대표 |
| 기록 상세 | `records-detail-outages`, RM `records-detail-episode` | 있음 | 있음 | 있음 (다크·라이트) | 있음 (다크·라이트) | **추가** |
| 기록 비교 | `records-compare` (두 측정 열·화살표) | **추가** | **추가** | **추가** (다크) | 없음 (반복 움직임 없음) | 상세로 대표 |
| 진단 기록 | `records-diagnoses` | **추가** | 없음 (진단 화면의 HC-D로 같은 부품 대표) | **추가** (라이트) | 없음 | 상세로 대표 |
| 리포트 | `report-preview`, 그림 `report-preview-figure` | 있음 | 있음 | 있음 (다크·라이트) | 있음 (다크·라이트) | **추가** |
| 설정 | `settings-normal`, 옮기는 중 `settings-moving` | 있음 | 있음 | 있음 (다크·라이트) | 있음 (`settings-moving` 라이트) | **추가** |
| 사용 조건 창 | `terms` | 있음 | 있음 | 있음 (다크) | 있음 (라이트) | **추가** |
| 대화상자: 저장 안 한 변경 | `dialog-unsaved` | **추가** | **추가** | **추가** (다크) | 대화상자 공통 부품, 아래 둘로 대표 | 라이선스와 고지로 대표 |
| 대화상자: DNS 비우기 확인 | `diagnose-flush-confirm` | **추가** | **추가** | **추가** (라이트) | 같음 | 같음 |
| 대화상자: 숨김 안내 | `hint-dialog` | **추가** | 있음 | **추가** (다크) | 있음 (다크) | 같음 |
| 대화상자: 기록 옮기기 확인 | `settings-move-confirm` | **추가** | **추가** | **추가** (다크) | 같음 | 같음 |
| 대화상자: 라이선스와 고지 | `about-legal-expanded` (가장 긴 내용, 문단 목록) | 있음 | 있음 | 있음 (라이트) | 있음 (다크) | **추가** |
| 트레이 메뉴 | 시스템이 그리는 Win32 메뉴 | 없음 (앱이 그리지 않음, 인수 7) | | | | |

**전환 프레임 추가**: 설정 화면 진입, 동의 → 셸 전환 (기존 전환 프레임 장치, 각 한 묶음).

**산출** (설계 C1 [2-4]): 새 기본 장면 1개 × 4장 = 4장, 대체 모드 추가 42장 (첫 실행 7, 홈 3, 측정 3, 프로필 5, 진단 1, 기록 목록 2, 상세 1, 비교 3, 진단 기록 2, 리포트 1, 설정 1, 사용 조건 1, 대화상자 11, 라이선스와 고지 1), 전환 프레임 2묶음. 장면 실행 수는 캡처 수와 같다(한 실행에 한 장). 기존 캡처 646장에 46장(새 기본 4 + 대체 42)과 전환 프레임, 실행 시간은 장면당 약 3초로 잡으면 약 2.5분이 는다 (추정, 최종 게이트에서 실제 값을 적는다).

**대비 실측 추가** (설계 C2): `measure-live`, `measure-outage`, `firstrun-detected`, `profile-ok`, `settings-normal`, `terms` × 다크·라이트. 요소: 그 장면의 제목·본문 글·흐린 글·단추 글·정보 칩·배너 글, 놓인 유리 단계별.

### 재검토 (C4)

**1. 흑요석 미세 결 (M2 설계 5.5 "M11 재검토")**
- 비교: `docs/loop/m11/obsidian-grain-compare.png` — 기준 시안 `docs/design/reference/obsidian-dark.png`와 0.9.0 게이트 캡처 `matrix-m5-home-measuring-dark-100.png`의 같은 자리(제목 주변 배경)를 3배로 키움.
- 관찰: 시안 배경에는 촘촘한 점 결이 보인다. 지금 앱 배경에는 없다. 평평한 배경 칸의 밝기 표준편차가 시안 R·G·B 6.1·8.1·11.3, 지금 3.8·5.4·7.8 (결과 빛 번짐 차이를 함께 잰 값이라 결만의 수치는 아니다). 화면 전체 평균색은 비슷하다(시안 20·25·31, 지금 20·24·29).
- 판정: **시안과 다르다 (넣지 않은 상태).** 넣는 것은 디자인 변경이므로 사용자 결정을 기다린다. 넣는다면 배경 빛 층 위에 고정 점 결 이미지(토큰 불투명도) 한 층, 투명 효과 끔·고대비에서는 끈다, 캡처 매트릭스와 대비 시험을 다시 돈다.
- 후속: 사용자 결정 "M12에서 넣기" (2026-11-21).

**2. 콘텐츠를 제목 표시줄까지 넓히기 (M5 설계 4.2)**
- 판정: **넓히지 않음 유지.** 이유: 끌기 영역·캡션 단추 영역 처리, 창만 캡처(PrintWindow)와 자동화 경계에 새 위험이 생긴다. 기준 시안의 정보 구조에는 영향이 없다(시안도 제목 표시줄 영역을 쓰지 않는다). 설계 검토 1라운드에서 Codex도 타당하다고 봤다.
- 후속: 없음. 인수 4에서 제목 표시줄이 테마와 맞는지만 본다.

**3. 문구 점검 (M5 설계 4.4)**
- 사실: 설계가 말한 화면별 문구 묶음 `Screens/Text.cs`는 만들어지지 않았다. 문구는 각 화면 코드와 Core의 문구 묶음(`Diagnostics/DiagnosisTexts.cs`, `Report/ReportTexts.cs` 등)에 있다.
- 점검 결과 (2026-11-21, 읽기 전용): `docs/loop/m11/wording-review.md`. 높음 11건(단정 3, 오해 5, 사생활 3 — 가리지 않은 리포트 안내에 어댑터 이름·측정 ID가 빠짐), 중간 약 30건(용어 불일치: 끊김 "회"/"번", 응답 없음/손실/실패, 끊김/단절, 공유기/게이트웨이, 측정 구성/프로필, 후보 표현), 낮음 약 25건.
- 판정: 사용자 결정 "높음만" — 높음 11건을 고쳤다 (DECISIONS "M11 구현 중 사용자 결정"). 중간·낮음은 M12.

## 3. 완성 조건 3~6

| 조건 | 근거 | M11 전 | M11에서 |
| --- | --- | --- | --- |
| 3. 설정 없이 측정 시작 | `M5FlowTests.FirstMeasurementStartsInOneAction`, M10 실측(동의 → 시작) | 증거 있음 (데모, M10 실측은 `507b98a` 빌드) | B4, 최종 exe 실측 D3 |
| 4. 자동 테스트·CI·깨끗한 클론 | CI 성공 `7768499`, 깨끗한 클론 `f501773`·`75203c9`·`1e73d6a` | 증거 있음 (M10 커밋) | 최종 X에서 둘 다 |
| 5. 모든 마일스톤 G1~G5와 PR | STATE "지난 마일스톤의 게이트" M2~M10. PR: M0~M4는 옛 비공개 저장소 `Ow-wL/NetPulse-private`, M5~M10은 공개 저장소 `Ow-wL/NetPulse` #1~#6 (모두 병합). G4 라운드 상한 예외는 모두 사용자 승인 기록 | 증거 있음 | M11 PR |
| 6. 맨 위 커밋에서 전체 게이트 | — | 아직 | D0~D1 |

## 4. 열린 항목 판정 (설계 G)

| 항목 | 기록 위치 | 판정 |
| --- | --- | --- |
| 닫을 때 드문 0xC000027B (M5-1, M7-4, M9-4) | STATE "M9 이월 처리 상태" | 재현 없음, 계측 유지 → **한계** + 인수 12 |
| coreclr `CardBody.Set` 접근 위반 3건 (원인 미확인) | STATE "종료 충돌 자료" | 최종 게이트 기록에서 다시 보고, 없으면 **한계** + 인수 12 |
| 측정 화면 튐 (M8-5, 원인 미확정) | STATE | 같음 (인수 3) |
| 한도 뒤 남은 종료 작업의 실패 관찰 | DECISIONS "M10 G4 1~4라운드와 게이트" | **한계** |
| 실제 클립보드 시험이 클립보드 준비에 기댐 | 게이트 절차 | 절차에 명시, 유지 |
| 실측에서 트레이 메뉴 UIA 고르기 무시 | DECISIONS "M10 UI 시험 1차와 실측" | 인수 7 |
| 노트북 게이트 성능·타이밍 (825/834) | DECISIONS "M10 SDK 고정과 공개 버전 0.9.0" | M12 (프레임 기준·게이트 단축) |
| 금융 보안 프로그램 충돌 | 같음 | M12 (1.0.0 조건), 인수 11 |

## 5. UI 시험의 CI 제외 (설계 D4, M1 설계 "M11에서 다시 본다")

**판정: 계속 CI에서 빼고 데스크톱 게이트로 둔다.**
- 834건 약 50분, 완전 직렬.
- 대화형 데스크톱의 전경 창, 실제 클립보드, 작업 표시줄이 필요하다. 호스트 러너는 이것을 보장하지 않는다.
- 같은 커밋에서도 하드웨어에 따라 성능·타이밍 기준이 흔들렸다 (노트북 825/834, 성능 5·타이밍 3).
- M12의 게이트 단축(C→B→A)과 프레임 기준 재정의 뒤 다시 볼 수 있다.

## 6. 검증한 배포 파일 (설계 E)

| | 게시본 0.9.0 | M11 검증본 |
| --- | --- | --- |
| 커밋 | `5c30ef2af3ada2936eebed4fdb8ee7cb83808135` (태그 `v0.9.0`) | (최종 X) |
| manifest SHA-256 | `BF98200168000B9B8983598554CD82A9FB6DB91EB92E4492D1C9350D277D1848` | |
| zip | `NetPulse-0.9.0-win-x64.zip` 69,147,685 B, `5836596c06739843b210b833f79218b014b12dba0ae11fe3a3c9b3e6948b4a44` | |
| exe | `NetPulse-0.9.0-win-x64.exe` 183,409,386 B, `3896992bda7db02a8de54e9ed3c894ec87eaef8f07bdd496d3a984b238a0719f` | |
| 제품·배포 입력 차이 (`5c30ef2..X`) | — | (분류: 가 / 나 / 다) |

## 7. M11에 맡겨진 일

| 출처 | 일 | 이 문서 |
| --- | --- | --- |
| 기획서 8절 M11 행 | 맨 위 커밋 전체 게이트, 디자인 총점검, 완성 정의 점검, 버전·해시, 인수 체크리스트 | 3절 6, 2절, 1·3절, 6절, `acceptance-checklist.md` |
| 기획서 4절 "데모만으로 통과시키지 않는 것" | 최종 배포 파일을 데모 없이 실측 시나리오 | 설계 D3 (결과는 공개 안 함, 여기에는 통과 여부만) |
| 기획서 7절 | M11에서 전 화면을 찍는다 | 2절 장면 포함표 |
| M1 설계 | UI 시험 CI 제외 재검토 | 5절 |
| M2 설계 5.5 | 흑요석 미세 결 | 2절 재검토 |
| M5 설계 4.2·4.4 | 제목 표시줄 넓히기, 문구 점검 | 2절 재검토 |
| M10 설계 | 깨끗한 Windows 미확인 | 인수 5 |
| M1 설계 | 다음 마일스톤이 없으면 PR 뒤 기록 커밋 하나 더 | M12가 있으므로 평소 절차 |
