# M12 문구 적용 기록 (설계 D)

`docs/loop/m11/wording-review.md`의 **중간·낮음** 항목을 `src`의 사용자에게 보이는 문자열에 적용한 기록이다. 식별자·자동화 ID·JSON 필드·파일 이름·열거 이름과 동작은 바꾸지 않았다. 시험은 돌리지 않았고(다른 기계에서 돌림), 빌드만 확인했다.

## 용어 결정

- 끊김 횟수 단위는 "번": "끊김 {n}번", "최대 연속 응답 없음 {n}번", 경로 맵 "응답 없음 {n}번 연속".
- 응답 없음 계열은 "응답 없음"(손실·실패 안 씀): "마지막 응답 없음", "최대 연속 응답 없음", 비율은 "응답 없음 비율". 홈 숫자 줄은 "응답 없음 {q}% ({n}번 중 {m}번)".
- 검사 오류는 "측정 오류": "측정 오류 {n}번 (응답 없음 비율 계산에서 뺌)". 진단 쪽 "검사"와 겹치지 않게.
- 프로필은 사용자에게 "측정 구성": "새 측정 구성", "이 측정 구성 삭제", "측정 구성 이름", "대상과 측정 구성".
- 게이트웨이: 처음 나올 때·검사 이름은 "공유기(게이트웨이)", 결과 줄은 "공유기: …".
- 범례 "공유기 지연"은 "로컬 대상 지연" (측정·기록 화면, 리포트 SVG).
- 원인 후보는 "가능성이 높은 곳". 구간 이름은 경로 맵 표현(`PathMapModel.PlaceText`)을 그대로 쓴다.
- 끊김/단절은 "끊김": "이 PC 연결 끊김 관측", "외부 연결 끊김 추정". 측정 중단은 "중간에 중단됨"(M11에서 바뀜).
- 기록 상태: "정상 종료 기록 없음", "정상 종료 기록을 쓰지 못함", "기록의 기본 정보"(머리 안 씀).
- "지난 진단"(마지막 진단·마지막 검사·지난 검사 안 씀). "응답 제한 시간"(띄어 씀).
- 타임라인 범위 표시는 "(측정 시작)" / "(측정 끝)".
- 다시 시도는 "다시 시도하세요.", 피동은 "관측되었습니다".
- 예외 형식 이름·API 이름은 사용자 글에 넣지 않는다.
- 상주 알림 "관측이 이어진 시간"은 끊김 길이에 쓰는 분·초 형식(`TimelineModel.AtLeast`, 초 아래 버림)을 쓴다.

## 중간

| 위치 | 전 | 후 | 처리 | 바뀐 시험 |
| --- | --- | --- | --- | --- |
| MeasurementHost (+ HomeScreen·MeasureScreen 앞말) | "측정을 시작하지 못했습니다 (IOException)" | 사유만: "기록 파일을 쓰지 못했습니다." / 그 밖은 "예상하지 못한 오류가 났습니다." (화면 앞말 "측정을 시작하지 못했습니다: "는 그대로) | 고침 | 없음 |
| MeasurementSession | "프로필 오류: no-targets, …" | "측정 구성 오류: 측정할 대상이 없습니다." (코드 대신 검증 문구를 이음) | 고침 | 없음 |
| HomeScreen, MeasureScreen, LiveSummary | "끊김 {n}회", 최근 목록 "{n}회" | "끊김 {n}번", "{n}번" | 고침 | M5FlowTests (결과·최근 줄) |
| MeasureScreen, RecordsScreen, PathMapModel | "최대 연속 실패 {n}회" / "최대 연속 손실" / "응답 없음 {n}회 연속" | "최대 연속 응답 없음 {n}번" / "응답 없음 {n}번 연속" (리포트는 이미 "최대 연속 응답 없음") | 고침 | M5FlowTests (카드 연속), M5ModelTests (노드 상세) |
| MeasureScreen, RecordsScreen | "마지막 실패" / "마지막 손실" | "마지막 응답 없음" | 고침 | 없음 |
| LiveSummary 숫자 줄 | "응답 성공 99.2%" | "응답 없음 0.8% ({n}번 중 {m}번)" (같은 자리의 성공·손실 수로 계산, 새 배관 없음) | 고침 | 없음 |
| MeasureScreen, RecordView, ReportBuilder, LiveSummary | "검사 오류 n회 (손실에서 뺌)" / "(분모에서 뺌)" / 최신값 "검사 오류" | "측정 오류 {n}번 (응답 없음 비율 계산에서 뺌)", 최신값·표 열 "측정 오류" | 고침 | M7ComparisonTests ("측정 오류를 뺀 요청이…") |
| ProfileEditor, FirstRunScreen (HomeScreen·SettingsScreen) | "대상과 프로필" / "프로필 이름" / "새 프로필" / "이 프로필 삭제" / "저장한 구성" | "대상과 측정 구성" / "측정 구성 이름" / "새 측정 구성" / "이 측정 구성 삭제" / "저장한 측정 구성" | 고침 (홈·설정은 이미 "측정 구성") | 없음 |
| QuickDiagnosis 검사 이름, DiagnosisTexts | "게이트웨이" / "기본 게이트웨이(공유기)" / "게이트웨이: 응답 없음…" | "공유기(게이트웨이)" / "공유기(게이트웨이)에 Ping 한 번" / "공유기: 응답 없음…", "공유기: 확인하지 못함" | 고침 ("공유기가 Ping에 답했습니다"는 이미 맞음) | 없음 |
| FirstRunScreen, DetectionPlanner | "다른 연결에도 게이트웨이가 {n}개 있습니다…" | "다른 네트워크 연결에도 공유기(게이트웨이)가 {n}개 더 있습니다…" | 고침 | M5ModelTests (vpn 안내) |
| MeasureScreen, RecordsScreen, TimelineSvg | 범례 "공유기 지연" | "로컬 대상 지연" | 고침 (TimelinePanel은 범례 글 없음, 주석만) | 없음 |
| PathMapPanel·PathMapModel / RecordView / ReportBuilder | "가능성이 높은 곳" / "후보: …" / 표 열 "구간 후보" | 모두 "가능성이 높은 곳", 후보 없음은 "가능성이 높은 곳 없음 — {사유}" | 고침 (경로 맵은 이미 맞음) | M8BuilderTests (후보 문구 시작) |
| RecordView (화면 줄·복사) vs PathMapModel | "공유기 이후 (인터넷 쪽)" / "PC~공유기 구간 또는 공유기" | `PlaceText` 그대로: "공유기 너머" / "이 PC와 공유기 사이 또는 공유기" / "이 PC 쪽 링크"(복사) | 고침 (경로 맵 쪽 문구는 그대로라 M5ModelTests 단정은 바뀌지 않음) | 없음 |
| RecordView PcLink | "후보: 이 PC의 유선 링크 (어댑터 다운 관측. 원인이 링크라는 단정은 아님)" | "가능성이 높은 곳: 이 PC의 유선 연결 (어댑터 연결 끊김이 관측됨. 원인이라고 단정할 수는 없음)" | 고침 | 없음 |
| RecordView 판정 이름, Profile 경고 | "로컬 링크 단절 관측", "외부 연결 단절 추정", "'외부 연결 단절'을 판단…" | "이 PC 연결 끊김 관측", "외부 연결 끊김 추정", "'외부 연결 끊김 추정'을 판정…" | 고침 | M8BuilderTests (판정 이름) |
| DiagnosisTexts, QuickDiagnosis | "어댑터 링크가 내려가 있음", "링크가 올라가 있고…", "링크가 지금 내려가 있습니다", "어댑터가 내려가 있습니다" | "네트워크 어댑터 연결(케이블·Wi‑Fi)이 끊겨 있음", "연결이 살아 있고 최근 30분 끊긴 기록이 없습니다.", "…이 지금 끊겨 있습니다.", "어댑터 연결이 끊겨 있습니다" | 고침 | 없음 |
| QuickDiagnosis, DiagnosisTexts | "인터넷은 되지만 예외가 관측됐습니다 (공유기 무응답 또는 로그인 확인 응답)." | "인터넷은 되지만 확인할 점이 있습니다 (공유기가 Ping에 답하지 않았거나 로그인 페이지 같은 응답을 받음).", 머리말 "…확인할 점이 있음" | 고침 | 없음 |
| RecordsScreen, RecordLibrary, ReportBuilder (+ ReportScreen 목록) | "끝 기록 없음" | "정상 종료 기록 없음" | 고침 | M7RecordViewTests (끝 사유), M8BuilderTests (summary.end) |
| RecordView | "마무리 기록을 쓰지 못함" | "정상 종료 기록을 쓰지 못함" | 고침 | M7RecordViewTests (저장 상태) |
| RecordsScreen, MeasurementComparison, RecordLocator (+ RecordView·RecordSchema) | "기록 머리를 읽지 못했습니다." 등 "머리" | "기록의 기본 정보를 읽지 못했습니다.", "기본 정보가 다른 조각…", "(기록의 기본 정보 파일이 없음)" | 고침 | M7LibraryTests (조각 안내) |
| MeasureScreen, PathMapModel | "응답 기준선이 없는 대상", "응답 기준선 없음" | "아직 한 번도 응답하지 않은 대상", "아직 한 번도 응답하지 않음", "로컬 대상이 아직 한 번도 응답하지 않아 구간을 판단하지 않습니다." | 고침 | M5ModelTests (no-local-baseline 사유) |
| AppShell.Resident | "관측 {n}번. 측정 화면의…" | "끊김이 {n}번 관측되었습니다. 측정 화면의…" | 고침 | 없음 |
| SettingsScreen | "…끝까지 돕니다." | "…끝까지 진행합니다." | 고침 | 없음 |
| DiagnoseScreen (화면 읽기 이름) | "검사 일곱" | "검사 7가지" | 고침 | 없음 |
| ReportTexts 가림 안내 | "…사업자는 서로 다른지만, 측정 ID는…" | "…사업자는 서로 다른지만 알 수 있게 '사업자 A·B'로, 어댑터는 종류와 순번으로, 측정 ID는…" | 고침 | 없음 |
| HomeScreen, DiagnoseScreen, DiagnosisView | "마지막 진단" / "마지막 검사" / "지난 검사와" | "지난 진단" / "지난 진단과" ("지난 진단 기록과"는 그대로) | 고침 | M6DiagnosisTests, M6FlowTests ("지난 진단과 같습니다.") |
| ReportBuilder | "하위 구간마다 아래 줄에" | "하위 구간별로 아래 줄에 적음" | 고침 | M8FlowTests (episode.candidate) |
| ReportBuilder | "관측 충족" | "판단할 수 있는 관측 비율" | 고침 | 없음 |
| RecordsScreen | "…비교가 부정확합니다." | "…비교가 부정확할 수 있습니다." | 고침 | 없음 |
| LiveAppWorld, FirstRunScreen, DetectionPlanner | "연결 정보를 읽지 못했습니다: " + 예외 이름, "경로를 조회하지 못했습니다: GetBestRoute_n" | "연결 정보를 읽지 못했습니다. 다시 확인하세요.", 경로는 사유 표(`DiagnosisTexts.Reason`)로 "경로를 조회하지 못했습니다 (오류 n). 다시 확인하세요." | 고침 (LiveAppWorld는 그 문구만) | 없음 |

## 낮음

| 위치 | 전 | 후 | 처리 | 바뀐 시험 |
| --- | --- | --- | --- | --- |
| RecordsScreen, RecordView, ReportBuilder, MeasurementComparison, Profile, DiagnosisTexts | "응답 제한시간" / "제한시간" / "응답 제한 {n}초" | "응답 제한 시간", "제한 시간" | 고침 (ProfileEditor는 이미 맞음) | 없음 |
| QuickDiagnosis | "간헐 끊김이면…" / "간헐적으로 끊긴다면 실시간 측정으로…" | "가끔 끊긴다면 측정으로 기록하세요." | 고침 | 없음 |
| ReportBuilder, RecordsScreen | "이 측정 중에 한/돌린 빠른 진단이 없습니다." | "…실행한 빠른 진단이 없습니다." (진단 기록 빈 글 "돌리면"도 "실행하면") | 고침 | M8BuilderTests (진단 없음 글) |
| RecordsScreen, ReportBuilder | "(측정 처음)" / "(기록 끝)" | "(측정 시작)" / "(측정 끝)" | 고침 | 없음 |
| ReportBuilder (RecordView) | "알림 {n}건" | "기록 문제 {n}건" | 고침 (RecordView에는 해당 문구가 없음: "알림: {코드}"는 모르는 등급 알림 코드용이라 그대로) | M8BuilderTests, M8FlowTests (condition.damage) |
| ResidentNotifier | "관측이 이어진 시간 {n}초" | "관측이 이어진 시간 {12분 34초}" (`TimelineModel.AtLeast`) | 고침 | 없음 |
| MeasurementAnalysis | "외부 대상의 응답 손실이 7.3%로 많습니다." | "외부 대상의 응답 없음이 7.3%(100번 중 약 7번)로 많습니다." (같은 꼴의 "…%입니다", "…조금 있었습니다"도) | 고침 | 없음 |
| HomeScreen (화면 읽기 이름) | "연결 품질 " + 등급 | "등급 " + 등급 | 고침 | 없음 |
| HomeScreen vs RecordsScreen | "분석 중" / "분석 중…" | "분석 중" | 고침 | 없음 |
| SettingsScreen vs DiagnoseScreen·RecordsScreen | "다시 시도하세요." / "다시 시도해 주세요." | "다시 시도하세요." | 고침 | 없음 |
| ResidentNotifier 등 | "관측되었습니다" / "관측됐습니다" | "관측되었습니다" (MeasurementAnalysis, ReportTexts) | 고침 (ResidentNotifier는 이미 바뀜) | 없음 |
| SettingsScreen | "…파일이 있습니다(기록은…)" | "…파일이 있습니다 (기록은…)" | 고침 | 없음 |
| SettingsScreen | "…새 판이라…" | "…새 버전이라…" | 고침 | 없음 |
| MeasureScreen | "저장소가 회복되면 기록에 씁니다." | "저장할 수 있게 되면 기록에 씁니다." | 고침 | 없음 |
| RecordsScreen | "(줄을 열면 폴더를 열어 볼 수 있습니다)", "…줄을 펼치면 주변 회차" | "(항목을 열면 폴더를 볼 수 있습니다)", "줄을 펼치면 주변 회차가 보입니다" | 고침 | 없음 |
| RecordsScreen (화면 읽기 이름) | "가장 높은 창 {ms}" | "5분 구간 중 가장 높은 값 {ms}" | 고침 | 없음 |
| RecordsScreen | "표시 임계(…)" | "표시 기준(…)" | 고침 | 없음 |
| RecordFolderRules | "{name}와(과) 같은 폴더", "{name}를 품은 폴더" | "{name}와 같은 폴더", "{name}를 포함하는 폴더" (이름이 모두 받침 없는 말로 끝남) | 고침 | 없음 |
| RecordFolderRules | "바로 가기 연결(정션·심볼릭 링크)이…" | "연결 폴더(정션·심볼릭 링크)가…" | 고침 | 없음 |
| ReportTexts | 목록 안내의 세미콜론 | "…먼저 시작한 것. 끝을 모르는 끊김은 최소 길이로 셈)" | 고침 | 없음 |
| ReportTexts | "관측했으나 이벤트 없음", "수집 미지원", "수집 능력: 접근 거부", "수집 능력: 미지원" | "관측했지만 끊김 기록 없음", "이 어댑터는 기록을 지원하지 않음", "기록을 읽을 권한 없음", "이 어댑터는 기록을 지원하지 않음" | 고침 | M8BuilderTests, M8FlowTests (link.state) |
| SettingsScreen, QuickDiagnosis | "외부 TCP 주소 1", "외부 TCP 443" | "확인할 외부 주소 1", "외부 연결(TCP 443)" | 고침 | DiagnosisTests (결론 2곳), M6DiagnosisTests (관측 범위) |
| RecordReader | "measurement.json이 없거나 읽을 수 없습니다." | "기록의 기본 정보 파일이 없거나 읽을 수 없습니다." | 고침 | 없음 |
| FirstRunScreen | 로컬 대상 "{이름} {주소}" | 그대로 | 그대로: 첫 실행에서 어느 장비를 잴지 사용자가 주소로 확인해야 함 (캡처를 나눌 때는 사용자가 판단) | 없음 |
| SettingsScreen | 카드 제목 "보기" | "화면 표시" | 고침 | 없음 |
| DiagnoseScreen | "…다시 돌립니다." | "…다시 실행합니다." | 고침 | 없음 |

## 함께 바꾼 것 (같은 용어 결정, 목록 밖)

- MeasureScreen 대상 카드 "손실 {비율}" → "응답 없음 {비율}" (M5FlowTests 카드 손실 단정 함께 바꿈).
- ReportBuilder 대상별 표 열 "손실률" → "응답 없음 비율", 설명 "측정 오류는 응답 없음과 구별하고 응답 없음 비율 계산에서 뺍니다."
- RecordsScreen 비교: "최대 연속 손실" → "최대 연속 응답 없음", "검사 오류" → "측정 오류", "연속 손실 횟수" → "연속 응답 없음 횟수", "손실률의 뜻" → "응답 없음 비율의 뜻". MeasurementComparison "끝을 모르는 연속 손실" → "끝을 모르는 연속 응답 없음".
- RecordView 회차 칸·PathMapModel 노드 "검사 오류" → "측정 오류".
- ProfileStore 저장 거부 글 "프로필 '…' 오류" → "측정 구성 '…' 오류".
- RecordLocator "(끝 기록이 없으면 …)" → "(정상 종료 기록이 없으면 …)".

## 바꾸지 않은 것

- 진단 사유 "검사 오류 (데모)": 진단의 "검사"라 그대로.
- 경로 맵 `PlaceText`의 "이 PC 쪽 링크": 경로 맵 쪽 기준 문구이고 시험(M5ModelTests)이 단정하므로 그대로. 기록 화면 PcLink 줄만 제안대로 "이 PC의 유선 연결".
- 내부 인수 검사 예외(EpisodeDetail, MeasurementAnalysis의 "머리가 없는 기록…"): 화면에 보이지 않는 개발자용 글.
- 개발용 전시 창(ShowcaseWindow "끊김 0회"): 점검 범위 밖.

## 빌드

- HEAD + 이 변경만 담은 사본에서 App, Core.Tests, Design.Tests, Platform.Tests, UiTests 다섯 프로젝트 Debug 빌드: 경고 0, 오류 0.
- 작업 트리 그대로의 빌드는 다른 작업이 진행 중인 추적 안 된 파일(`MeasurementSession.Signal.cs` 등)의 오류로 실패한다 (이 변경과 무관).
