# 루프 상태

재개할 때는 이 파일과 `docs/loop-plan.md`를 먼저 읽는다.

## 현재

| 항목 | 값 |
| --- | --- |
| 마일스톤 | M8 리포트 |
| 단계 | G4 8라운드 APPROVE(`350b609`) → 게이트 통과(UI 두 번째 실행, DECISIONS "M8 게이트"). 다음: 마감 스크립트로 PR → 사용자 병합 |
| 브랜치 | `loop/m8-report` (설계 커밋 위에 M7 병합 뒤 `main`(`573d24b`)을 병합) |
| 설계 검토 라운드 | 2 / 2 (1라운드 REVISE 13건 → 2판, 2라운드 APPROVE + 구현 중 결정 7건) |
| diff 리뷰 라운드 | 3 / 3 + 사용자 승인 예외 (1라운드 `ec27e1a` 차단 3·중요 8·사소 1, 2라운드 `e2c5733` 차단 3·중요 2·사소 2, 3라운드 `c31f879` 차단 2·중요 2·사소 1, 4라운드 `b9775a0` 차단 1·중요 2·사소 1, 5라운드 `233ae0e` b APPROVE·a 차단 1·중요 1, 6라운드 `0e958f4` a 차단 1·중요 2 → 메모 방식 C, 7라운드 `26bd0be` a·b APPROVE·사소 3, 주석 1건은 8라운드 대상에서 고침, 8라운드 `350b609` APPROVE) |
| 누적 실패 | 0 / 8 |
| PR | 없음 (G4 승인 뒤 마감 스크립트가 만든다) |
| M7 | 끝: PR Ow-wL/NetPulse#3 사용자 병합 (`573d24b`) |

**저장소 이전 (2026-11-14, `DECISIONS.md` "공개 저장소 이전")**: 공개 저장소 `Ow-wL/NetPulse`는 이력을 다시 쓴 새 저장소다. 아래 M0~M4 기록의 SHA·PR 번호는 옛 저장소(`Ow-wL/NetPulse-private`, 비공개) 기준이다.

## 지난 마일스톤의 게이트 (M8, 2026-11-17)

| 항목 | 값 |
| --- | --- |
| 승인받은 코드 커밋 X | `350b609d3169705cbf04f9c974263115df8a3377` |
| G1·G2 | `build/build.ps1 -Gate`: Core 467/467, Design 111/111, Platform 29/29, 미실행 0 |
| G3·G5 | `build/ui-test.ps1`: 배포본 UI 시험 635/635 (`Kind=Live` 제외, 두 번째 실행. 첫 번째는 M5 nav 62.8 ms·M6 시간 경쟁 1개로 633/635, DECISIONS "M8 게이트"). G5 통과, 필수 누락 0, 매트릭스 498 (M2 44), 장면 M5 30·M6 26·M7 29·M8 17 |
| 리포트 bench | enter·scroll·options 최대 32.7·41.1·27.9 ms (33 ms 초과 scroll 1회), worst 79.0 ms·1회 (기준 80 ms·6회), worst-scroll 50.9 ms·1회 (기준 80 ms·2회, 둘 다 사용자 결정), 최고 전용 메모리 386 MB. 문서 만들기 보통 17~108 ms, 최악(구간 19,600개) 51~94 ms |
| 기록 bench | enter·open·timeline·switch·compare 최대 23.5·35.7·32.8·34.7·25.9 ms (33 ms 초과 open 1회·switch 1회), 최고 전용 메모리 283 MB |
| 측정 bench | nav 최대 33.4 ms (33 ms 초과 1회), 그 밖 구간 23.6 ms 이하. 진단 구간 최대 22.9 ms |
| G4 | `.loop/reviews/m8-g4-r8-answer.md` APPROVE (차단 0). 4~8라운드는 사용자 승인 예외 |
| manifest.json SHA-256 | `D06CB49436DA21170F8AF29EC4B0DEE96FBA3A35074FA33C3C0BB63AE74F6740` |

## 지난 마일스톤의 게이트 (M7, 2026-11-16)

| 항목 | 값 |
| --- | --- |
| 승인받은 코드 커밋 X | `b58f53195252228afcfb9b66698d4647b3d0cffa` |
| G1·G2 | `build/build.ps1 -Gate`: Core 388/388, Design 109/109, Platform 29/29, 미실행 0 |
| G3·G5 | `build/ui-test.ps1`: 배포본 UI 시험 539/539 (`Kind=Live` 제외, 세 번째 실행. 앞의 두 번은 닫을 때 비정상 종료 1회·클립보드 형식으로 건너뜀, DECISIONS "M7 게이트"). G5 통과, 필수 누락 0, 매트릭스 420 (M2 44), M7 장면 29 |
| 기록 bench | enter·open·timeline·switch·compare 최대 32.0·36.6·27.5·26.2·37.5 ms (33 ms 초과 open 1회·compare 1회), 분석 중 이동·다시 들어옴·메모 뒤 최대 20.1·16.3·10.0 ms, 최고 전용 메모리 274 MB |
| 측정 bench | nav 최대 34.2 ms (33 ms 초과 1회), 그 밖 구간 24.2 ms 이하. 진단 구간 최대 22.0 ms |
| G4 | `.loop/reviews/m7-g4-r7-answer.md` APPROVE (차단 0). 4~7라운드는 사용자 승인 예외 |
| manifest.json SHA-256 | `48C0E9DB8C3B400111736F1BCCEA4722C56CC8CA3CCC7720A823C442C38EC37B` |

## 지난 마일스톤의 게이트 (M6)

| 항목 | 값 |
| --- | --- |
| 승인받은 코드 커밋 X | `426642e1a23f2b5fe362a6a5c77fc513de9b8bc3` |
| G1·G2 | `build/build.ps1 -Gate`: Core 330/330, Design 109/109, Platform 29/29, 미실행 0 |
| G3·G5 | `build/ui-test.ps1`: 배포본 UI 시험 389/389 (`Kind=Live` 제외). G5 묶음 통과, 필수 누락 0, 매트릭스 293 (M2 44). bench: 측정 화면 nav 최대 36.7 ms (33 ms 초과 1회), diag-enter·run·reenter 최대 17.5·27.4·19.6 ms, 전용 메모리 212 MB, 실행 중 최고 218 MB |
| G4 | `.loop/reviews/m6-g4-r4-answer.md` APPROVE (차단 0). 1라운드 13건, 2라운드 5건, 3라운드 4건 반영 |
| 실측 시나리오 (설계 7.3) | 이 PC에서 통과 (`.loop/shots/m6-live`, 공개 안 함). 시험 단언 하나를 임시로 고쳐 실행 (미해결 지적 5) |
| manifest.json SHA-256 | `02ED20A9CE68BE6616F26A53ED435C72CBDE00E4CE1BA26EDD47FF236AA20E5F` |

## 지난 마일스톤의 게이트 (M5)

| 항목 | 값 |
| --- | --- |
| 승인받은 코드 커밋 X | `67f071b239be5f7d2bcd9ddd1386d63b1b62e550` (3라운드 승인 `12bbb6b` + 시험 수정) |
| G1·G2 | `build/build.ps1 -Gate`: Core 302/302, Design 109/109, Platform 29/29, 미실행 0 |
| G3·G5 | `build/ui-test.ps1`: 배포본 UI 시험 244/244 (실측 시나리오 클래스만 제외). G5 묶음 통과, 필수 누락 0, M5 매트릭스 134, M2 매트릭스 44. 측정 화면 bench (12시간 42,600회차): idle-live 95백분위 6.2 ms, 전환 최대 17.6·23.9·34.3 ms (zoom-full·pan·nav). 이전 실행에서는 nav 최대 48.0 ms도 나왔다 |
| 실측 시나리오 (설계 7.3) | 이 PC에서 통과 (`.loop/shots/m5-live`, 공개 안 함) |
| G4 | `.loop/reviews/m5-g4-r3-answer.md` APPROVE, 시험 수정은 `m5-g4-r4-answer.md` APPROVE (차단 0) |
| manifest.json SHA-256 | `11357843C4D0B1B4607E375062A40D5EAEF9C4C0A83E49EC4C1B4AC6B8AE55E7` |

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

## 미해결 지적 (M2 G4 3라운드, 차단 아님 → M5에서 처리함: 1은 `Theme.ShutdownAsync`, 2·3은 재질·대비 범위 조정)

1. 중요: 창을 닫을 때 진행 중인 원형 전환의 정리 완료를 기다리지 않는다 (`Theme.Detach` 직후 창 닫기). 전환 완료 작업을 보관하고 닫기 전에 기다린다. 실제 종료 오류는 재현되지 않았다.
2. 중요: 대화상자 뒤 범위에 글자·그래프색이 들어 있지 않다. 넣으면 대화상자 위 `muted` 대비가 계산상 약 4.40으로 기준(4.5) 아래다. 대화상자 배후 범위와 실측 장면을 넓히고 재질을 조정한다.
3. 사소: 라이트의 세 재질 두께 구분이 약하다 (가독성 때문에 얇은 유리 불투명도를 올린 결과). 테두리·틴트·그림자로 조정한다.

## 미해결 지적 (M3 G4 3라운드, 차단 아님 → M4에서 처리함, 커밋 `2a1cc89`)

1. 중요: 복귀와 직전 표본의 경과 시간이 같으면(단조 시계 정지, 밀리초 절삭) 복귀 경계를 놓쳐 거짓 `clock` 이벤트가 남는다 (`GapTracker.CheckClock`의 `to > lastClockMs`). 아직 반영하지 않은 복귀를 따로 기억하고 같은 시각 시험을 더한다. 회차·손실 집계에는 영향 없음.
2. 중요: 시험의 쓰기 진입 신호가 "마지막 실패 완료"를 보장하지 않는다 (`RecordFakes` 횟수는 고장 결정 전에 오름). 실패 완료·배출 시도 완료 신호로 단계를 고정한다 (`ReviewR1Tests` 89·120행 부근).
3. 중요: `SuspendGapSurvivesWhenRoundsAfterResumeAreSealedAsUnprocessed`는 원래 경쟁(꺼낸 뒤 분류 잠금 전에 마감)을 재현하지 못한다. 꺼냄과 분류 사이에 끼어드는 시험 지점을 두어 고정한다. 코드는 같은 잠금으로 해결됨.
- 참고: 요약 게시가 시작된 뒤(이름 바꾸기)는 종료가 제한 없이 기다린다. 일관성을 위한 선택이며 "종료 기한은 게시 시작 전까지"로 표현한다.

## 미해결 지적 (M4 G4 5·6라운드, 차단 아님 → M5 A단계에서 처리함)

1. 중요: 링크 표본 마감 검사의 두 위치(받을 때, 큐에서 꺼낼 때)를 각각 고정해 시험한다 (`SessionLinkTests`).
2. 중요: 넘침 경쟁 시험(`LateOverflowRegistrationCannotMergeDownsAcrossTheLostSample`)이 큐 배출·재관측 생산자 진입을 `Task.Delay(200)`에 기댄다. 신호로 확인하고, 분리된 두 다운 구간의 존재·경계를 단언한다.
3. 중요 (새 공개 저장소 첫 CI에서 발견): `OverflowIsRecorded`가 CI에서 한 번 시간 초과로 실패했다 (회차마다 알림 3000개를 넣고 "관측" 조건을 기다림). 같은 코드가 옛 저장소 PR CI와 로컬 15회 반복에서는 통과했고, 재실행에서 통과. 느린 실행기에서 시간에 민감한 시험이다. 시험을 신호 기반으로 바꾸거나, 넘침 중 처리 지연이 제품 동작 문제인지 먼저 확인한다.

## M5 이월 처리 상태 (M6 C단계)

| 이월 | 상태 |
| --- | --- |
| 1 종료 충돌 | `AppDriver` 모든 종료 경로(시작 실패·Close·WaitExit·Dispose)에서 비정상 종료면 증거(`crash-*/`: 앱 기록, 응용 프로그램 로그, 오류 보고 폴더와 그 서명)를 남기고 실패. 진단 중·비우기 중·테마 전환 중 닫기 각 5회 시험 작성 (실행 전). 이 PC의 응용 프로그램 로그·오류 보고를 읽어 본 결과는 아래 |
| 2 bench nav 여유 | bench에 진단 구간 셋 추가. 3회 실행·최대값 보고는 UI 시험 때 |
| 3 관측 종류 전환 | 끝: Core `ObservationWatch`(지금 장면에서 종류 계산, 종류별 전환), 앱 틀이 씀. G2 4건 |
| 4 시험 대기 조건 | 끝: 사본 커서를 버퍼 맨 앞부터, `SessionLinkTests.Rounds()`가 게시까지 기다림. Core 3회 연속 통과 |

### 종료 충돌 자료 (2026-11-14, 이 PC의 Windows 기록을 읽기만 함)

- 응용 프로그램 로그의 NetPulse.exe 비정상 종료: 11-13 34건, 11-14 41건 (CoreMessagingXP.dll·Microsoft.UI.Xaml.dll 0xC000027B 대부분, coreclr.dll 0xC0000005 3건). 오류 보고 23건의 원래 HRESULT(저장된 예외): combase.dll 80131509(관리 코드 InvalidOperationException이 WinRT 경계를 넘음) 8건, 80004002 6건, 80070057 4건, 8000FFFF 1건, 88000FA8 1건, coreclr 0xC0000005 3건.
- coreclr 3건(11-14 17시대)은 .NET 런타임 기록에 스택이 있다: 홈 화면의 최근 측정 변경 알림(디스패처 큐) → `CardBody.Set`의 `Parent` 읽기에서 접근 위반. 재현 조건은 아직 모름.
- M5까지의 UI 시험 중 M5 시험 다수가 `Close()`의 종료 코드를 확인하지 않아 이 종료들이 시험 실패로 드러나지 않았을 수 있다 → 이제 모든 경로에서 실패시킨다.
- 같은 날 저녁 M6 장면·전체 UI 시험(약 450회 실행) 동안에는 새 비정상 종료 기록이 없다. "재현 없음"을 해결로 기록하지 않는다. 덤프는 없다(덤프 수집 설정은 시스템 설정이라 바꾸지 않음).
- 별개: 11-14 03시대 단일 파일 실행 파일의 압축 풀기 실패 94건("I/O failure when writing extracted files")이 한꺼번에 있다. 그 시각의 디스크·임시 폴더 상태 문제로 보이며 그 뒤 재발 없음 (추정).

## 미해결 지적 (M5 G4 3·4라운드, 차단 아님 → M6 C단계에서 처리, M6 설계 6절)

1. 중요: 창을 닫을 때 드물게 WinUI 내부 예외(0xC000027B, CoreMessagingXP.dll)로 끝난 일이 게이트에서 한 번 있었다. 원인 미확정, 닫은 뒤 큐 작업·알림 타이머를 끊는 완화만 했다. 다시 나면 UI 시험이 남기는 `crash-*.jsonl`(처리되지 않은 예외의 HRESULT·스택)과 네이티브 덤프로 원인을 찾는다.
2. 사소: 측정 화면 bench의 nav(첫 홈 이동) 최대가 실행마다 34~48 ms로 기준 50 ms에 여유가 작다. 반복 측정과 첫 표시 비용 분석.
3. 사소: 관측 종류가 바뀌는 순간 상태 알약이 한 번 이전 종류로 그려지고, 외부 응답 없음 → 링크 끊김으로 바로 바뀌면 새 종류의 알림이 나오지 않는다 (`AppShell.Render`). 지금 장면에서 계산한 값으로 그리고 종류별 전환을 따로 추적한다.
4. 중요(시험): `RoundItemsArriveWithTheirRouteEventsInRecordOrder`가 시작 직후 사본 커서를 잡아 0회차가 빠질 수 있다 → 커서를 0부터. `SessionLinkTests`의 `Rounds()`도 관측만 기다리므로 게시까지 기다리게 맞춘다 (G4 4라운드).

## 미해결 지적 (M8 G4 7라운드, 사소 → 다음 마일스톤)

1. (고침) 사소: `Redactor` 클래스 설명의 메모 패턴 가림 문장 → G4 8라운드 대상 커밋에서 고침.
2. 사소(시험): 문서 만들기 bench(`M8G5Tests.ReportBuildStaysWithinBudget`)가 가리지 않은 문서를 `Guard.Check`에 넘겨 안전망 검사 시간이 실제로 측정되지 않는다. 가린 문서의 검사 구간을 따로 잰다.
3. 사소(시험): 키보드 시험이 막힌 메모 스위치를 Tab이 건너뛰는지 확인하지 않고(목적지까지 Tab), 가리기 해제 → 메모 켜기(Space) → 다시 가리기를 키보드로 확인하지 않는다. 흐름 시험의 다시 가리기 단계에 UIA `IsEnabled`·ToggleState 검사도 붙인다.
4. 중요(시험, M7부터): `M7LibraryTests.OpenRequestsGoBeforeTheRestOfTheList`가 CI에서 가끔 실패한다(M7 병합 뒤 main, M8 PR). 열기 요청 전에 작은 기록 12개의 목록 분석이 다 끝나면 "끝난 분석 3 이하" 단언이 깨진다. 분석을 막았다 푸는 fixture로 열기 요청이 큐에 들어간 시점을 고정한다.
5. 게이트 관찰 (원인 미확정): M8 게이트 1회차 측정 화면 튐(idle-live 최대 198 ms, nav 62.8 ms). C 이전 앱과 번갈아 비교해도 원인을 가리지 못했다. 프레임 지연·GC·스레드를 시간축으로 맞춰 본다.

## 미해결 지적 (M7 G4 5·7라운드와 게이트, 차단 아님 → 다음 마일스톤)

1. 사소: 기록 화면에서 메모 저장이 재시도 중일 때 목록으로 나갔다가 같은 상세로 돌아오고, 새 저장 없이 그 저장이 실패하면 다시 그릴 계기가 없어 "저장하는 중…"이 남을 수 있다 (`RecordsScreen.SaveMemoAsync` 실패 쪽은 `Here()`가 거짓이라 다시 그리지 않음). 오래된 요청의 편집 상태 변경은 막되, 지금 상세가 같은 출처·표시이고 더 새 저장이 없으면 보관된 마지막 상태를 다시 그린다. 시험: 기존 실패 fixture에 저장 중 목록 복귀·재진입을 끼운다.
2. 중요 (7라운드): 기록 화면 메모 저장 성공 뒤 `RerenderKeepingFocus`보다 목록 갱신 알림(`RecordLibrary.Changed` → `Render`)의 일반 다시 그리기가 먼저 오면, 다른 칸(B)을 고치던 초점이 사라진다. 첫 다시 그리기 전에 초점을 잡고 갱신·다시 열기로 생기는 다시 그리기를 함께 보호한다. 성공 완료를 멈출 수 있는 시험(쓰기를 잠시 막았다 푸는 fixture 등)으로 확인한다.
3. 사소 (7라운드): 저장 완료 때 초점이 그 칸에 있으면 상태 글로 스크롤하는데, 사용자가 휠로 다른 곳을 보고 있어도 끌어온다. 저장 시작 뒤 사용자 스크롤 이동을 기억해 완료 때는 하지 않는다.
4. 게이트 관찰 (원인 미확정): 닫을 때 드물게 0xC000027B(combase `8001010e`). 닫기 단계(`StopAll`·`ui.Detach` 하위 단계·`Close` 전후·`Window.Closed`)에 스레드와 단계를 기록하고, 닫기를 시작할 때 기록 화면의 프레임 구독·큐를 직접 끊고, 줄이기 상태와 상관없이 모션을 모두 멈춘다. UI 자동화 쪽 원인(microsoft/microsoft-ui-xaml#11139) 여부는 닫기 요청 방식(`WindowPattern.Close`·`WM_CLOSE`)을 나눠 비교한다. 클립보드 시험의 보관 판정이 빈 값 형식(메모장의 `EnterpriseDataProtectionId`)을 막힘으로 보는 것도 검토한다.

## 미해결 지적 (M6 G4 4라운드, 차단 아님) → M7 UI 시험 1차에서 모두 반영

1. 중요(시험): 실제 클립보드 시험의 소유 판정은 번호 비교·글 일치 사이 간격과 "같은 글 + 다른 서식" 외부 복사를 막지 못한다. 공유 세션에서의 완전한 보존은 격리된 시험 세션이 필요하다. 복원 판정에 실제 쓰기 확인과 복사 후 번호·형식을 함께 쓴다.
2. 중요(시험): `AppDriver.PressEnter`는 전경 확인 뒤 두 `keybd_event` 사이에 창이 바뀌면 다른 프로그램으로 키가 갈 수 있다. `SendInput`으로 누름·뗌을 한 번에 보내고 전송 개수와 앱의 Enter 수신을 확인한다. 새 시험의 `inputs > baseline`은 앞의 `SetText`만으로도 참이라 Enter 수신 증거가 아니다.
3. 중요(시험): `Process.Kill()`은 이미 끝난 프로세스에 예외 없이 돌아올 수 있어 `killed=true`가 실제 강제 종료를 뜻하지 않는다. 시험 전용 종료 코드나 네이티브 종료 결과로 자발적 -1과 구분하고 회귀 시험을 둔다.
4. 사소: 홈 진단 카드도 진단 버전만 비교해 자정 뒤 "오늘"이 남을 수 있다. 홈 카드 캐시에 표시 날짜를 넣는다.
5. 중요(시험): `M6LiveRunTests`의 "실제 동작을 누르지 않았다" 단언이 실측에서 null인 데모 동작 기록을 배열로 읽어 실패한다. null 허용 + `lastAction` 없음으로 고친다 (임시 수정으로 실측 통과 확인, DECISIONS "M6 실측 시나리오").

## 다음 할 일

M8: 마감 스크립트로 PR을 만들고 사용자 병합을 기다린다. 다음 마일스톤에서 위 미해결 지적(M7·M8)과 측정 화면 튐(M8 게이트 1회차 idle-live 최대 198 ms·nav 62.8 ms, 원인 미확정)을 프레임 지연·GC·스레드를 시간축으로 맞춰 조사한다. 게이트 전에는 백그라운드 앱을 닫고 `clip`으로 글 형식만 넣는다.
