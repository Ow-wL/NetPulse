# M11 문구 점검 (설계 C4-3, 2026-11-21)

읽기 전용 점검 결과다. 사용자 결정(2026-11-21 "높음만"): 아래 **높음 11건은 M11에서 고쳤다**. 중간·낮음은 M12에서 다룬다.

- 범위: `src` 아래 .cs 파일의 한글 문자열 리터럴(주석 제외, 1,558개) 가운데 사용자에게 보이는 것. 빼고 본 것: 개발용 전시 창, 벤치 진단 문구, 명령줄 오류, 데모 시나리오 검증, 시험 주입 문구, 디자인 검증 예외, 리포트 금칙어 목록. `TERMS.txt`는 오타만 봤다(없음).
- 기준: 기획서 7.5와 M5 설계 4.4 — 결론 먼저, 쉬운 말, 단정하지 않음, 화면 사이 같은 낱말, 맞춤법·띄어쓰기, 사생활.
- 사실: M5 설계가 정한 화면별 문구 묶음 `Screens/Text.cs`는 만들어지지 않았다. 문구는 화면 코드(RecordsScreen 179, MeasureScreen 80, SettingsScreen 76, ReportScreen 61, ProfileEditor 58, HomeScreen 54, DiagnoseScreen 50, FirstRunScreen 36, AppShell 28, AppShell.Resident 20, TermsWindow 17개)와 Core 표시 문구(DiagnosisTexts, MeasurementAnalysis의 `Sentences`, RecordView, ReportTexts, ReportBuilder, QuickDiagnosis, PathMapModel 등)에 흩어져 있다.
- 경로 줄임: `App/` = `src/NetPulse.App/`, `Core/` = `src/NetPulse.Core/`. "시험" 열은 그 문구를 단정하는 시험(고치면 함께 바꿔야 함).
- 확인 못 함: `DetectionPlanner.cs:54`의 RouteError 내용, `EpisodeDetail.cs:53/59/65`·`RecordView.cs:142` 문구가 보이는 화면, 실제 배치(앱을 실행하지 않음).

## 높음 (오해·단정·사생활)

| 위치 | 지금 문구 | 문제 | 제안 | 시험 |
| --- | --- | --- | --- | --- |
| Core/Diagnostics/QuickDiagnosis.cs:271 | "이 PC의 네트워크 연결 자체에 문제가 있습니다" | 단정, 같은 결론 제목(DiagnosisTexts.cs:20 "…있을 수 있음")과 어긋남 | "이 PC의 네트워크 연결에 문제가 있을 수 있습니다" | 없음 |
| Core/Analysis/MeasurementAnalysis.cs:338 (337·340 공통 형식) | "인터넷 연결이 {n}초 이상 끊긴 적이 있습니다." | 단정, 초를 그대로 써 "754초"처럼 나옴 | "…{12분 34초} 이상 끊긴 것으로 보이는 때가 있습니다." | 없음 |
| Core/Diagnostics/QuickDiagnosis.cs:325 (진단 칩·복사 글) | 판정 이름 "확인" / "미측정" | "확인"이 "정상 확인"으로 읽힘, 진단을 "측정"이라고 씀 | "확인 필요" / "확인 못 함" | 없음 |
| Core/Report/ReportBuilder.cs:226 | 표 머리 "유효 응답" (값은 성공+응답 없음) | 응답 없음까지 센 수를 "응답"이라 부름 | "유효 시도" | 없음 |
| Core/Analysis/MeasurementComparison.cs:141, :147 | "유효한 응답이 100번보다 적습니다" | 같음 | "검사 오류를 뺀 요청이 100번보다 적습니다" | M7ComparisonTests.cs:118 |
| App/Screens/ReportScreen.cs:386 | "가리지 않은 리포트입니다. 주소와 이름이 그대로 들어갑니다." | 사생활: 어댑터 별칭·측정 ID도 그대로 들어감(Redactor.cs:58-59, :91) | "…대상 이름·주소, 사업자 이름, 네트워크 어댑터 이름, 측정 ID가 그대로 들어갑니다." | 없음 |
| App/Screens/ReportScreen.cs:741 (확인 대화상자) | "대상 주소와 이름, 사업자 이름이 그대로 들어갑니다. 내가 볼 때만 쓰세요." | 사생활: 같은 누락, 동의하는 자리 | 어댑터 이름·측정 ID 추가 | 없음 |
| Core/Report/ReportTexts.cs:30 | "…대상 이름·주소·사업자 이름이 그대로 들어 있습니다…" | 사생활: 같은 누락 (리포트 본문) | 어댑터 이름·측정 ID 추가 | 없음 |
| Core/Presentation/RecordView.cs:73 | 끝 사유 "중간에 끊김" | 측정 중단을 핵심어 "끊김"으로 씀 | "중간에 중단됨" | 없음 |
| Core/Report/ReportTexts.cs:116 | "끝 요약 없이 기록이 끊김" | 같은 충돌 | "끝 요약 없이 기록이 멈춤" | 없음 |
| App/Screens/RecordsScreen.cs:677-678 | 타임라인 단추 "앞으로"(과거) / "뒤로"(최근) | "앞으로"가 미래로 읽힘, 측정 화면은 "과거로"/"지금 쪽으로" | "과거로" / "최근 쪽으로" | 없음 |

## 중간 (용어 불일치·어색·잘림)

| 위치 | 지금 문구 | 문제 | 제안 | 시험 |
| --- | --- | --- | --- | --- |
| App/Screens/HomeScreen.cs:263, MeasureScreen.cs:293 + Core/Live/MeasurementHost.cs:244 | "측정을 시작하지 못했습니다: " + "측정을 시작하지 못했습니다 (IOException)" | 같은 말 두 번, 영어 예외 이름 | 호스트 쪽은 사유만 ("기록 파일을 쓰지 못했습니다") | 없음 |
| Core/Measurement/MeasurementSession.cs:145 | "프로필 오류: no-targets, …" | 내부 코드 노출, 프로필/측정 구성 섞임 | "측정 구성 오류: 측정할 대상이 없습니다." | 없음 |
| HomeScreen.cs:309, :382; MeasureScreen.cs:456; LiveSummary.cs:70 | "끊김 {n}회" | 단위 섞임 (기록·비교·리포트는 "번") | "끊김 {n}번" | M5FlowTests.cs:100, :114 |
| MeasureScreen.cs:343 / RecordsScreen.cs:764, :766, :1354 / ReportBuilder.cs:226 / PathMapModel.cs:84 | "최대 연속 실패 {n}회" / "최대 연속 손실" / "최대 연속 응답 없음" / "응답 없음 {n}회 연속" | 같은 개념 이름 셋, 단위 섞임 | "최대 연속 응답 없음 {n}번" | M5FlowTests.cs:105, M5ModelTests.cs:375 |
| MeasureScreen.cs:344 / RecordsScreen.cs:764 | "마지막 실패" / "마지막 손실" | 용어 불일치 | "마지막 응답 없음" | 없음 |
| Core/Presentation/LiveSummary.cs:84 | "응답 성공 {99.2%}" | 다른 화면은 손실 기준, 쉬운 말 짝 없음 | "응답 없음 {0.8%} ({n}번 중 {m}번)" | 없음 |
| MeasureScreen.cs:342 / RecordView.cs:190 / ReportBuilder.cs:244 / LiveSummary.cs:48 | "검사 오류 n회 (손실에서 뺌)" / "(분모에서 뺌)" | 설명이 화면마다 다름, "분모", 진단의 "검사"와 겹침 | "측정 오류 {n}번 (응답 없음 비율 계산에서 뺌)" | 없음 |
| ProfileEditor.cs:66, :130, :214, :225, :236, :272, :297; FirstRunScreen.cs:186; HomeScreen.cs:270-271; SettingsScreen.cs:135 | "측정 구성" / "대상과 프로필" / "프로필 이름" / "새 프로필" / "이 프로필 삭제" / "저장한 구성" | 같은 대상을 "측정 구성"과 "프로필"로 섞음 | 한 낱말로 정하기 | 단정문 없음 |
| QuickDiagnosis.cs:319; DiagnosisTexts.cs:34, :55, :82-83 | "게이트웨이" / "기본 게이트웨이(공유기)" / "공유기가 Ping에 답했습니다" 다음 줄 "게이트웨이: 응답 없음…" | 한 화면 안에서 공유기·게이트웨이 섞임 | 검사 이름 "공유기(게이트웨이)", 결과 줄 "공유기: …" | 없음 |
| FirstRunScreen.cs:193; DetectionPlanner.cs:69 | "다른 연결에도 게이트웨이가 {n}개 있습니다…" | 전문 용어, 뜻 불명확 | "다른 네트워크 연결에도 공유기(게이트웨이)가 {n}개 더 있습니다…" | M5ModelTests.cs:93 |
| MeasureScreen.cs:188; RecordsScreen.cs:1011; TimelineSvg.cs:35 | 범례 "공유기 지연" | 역할 이름은 "로컬 대상"인데 공유기라고 단정 | "로컬 대상 지연" | 없음 |
| PathMapPanel.cs:67, :151; PathMapModel.cs:190 / RecordView.cs:57-60, :286-289 / ReportBuilder.cs:208, :318 | "가능성이 높은 곳" / "후보: …" / "구간 후보" | 같은 개념 표현 셋 (설계는 "가능성이 높은 곳") | "가능성이 높은 곳"으로 통일 | 없음 |
| PathMapModel.cs:176-177 vs RecordView.cs:58-59, :287-288 | "공유기 너머" / "이 PC와 공유기 사이 또는 공유기" vs "공유기 이후 (인터넷 쪽)" / "PC~공유기 구간 또는 공유기" | 같은 구간 이름이 다름 | 경로 맵 쪽 표현으로 통일 | M5ModelTests.cs:363 |
| RecordView.cs:57 | "후보: 이 PC의 {유선} 링크 (어댑터 다운 관측. 원인이 링크라는 단정은 아님)" | 영어 "다운", 어색 | "가능성이 높은 곳: 이 PC의 유선 연결 (어댑터 연결 끊김이 관측됨. 원인이라고 단정할 수는 없음)" | 없음 |
| RecordView.cs:48-49; Profile.cs:78 | "로컬 링크 단절 관측", "외부 연결 단절 추정", 경고 "'외부 연결 단절'을 판단…" | 앱 전체는 "끊김", 경고의 판정 이름도 실제와 다름 | "이 PC 연결 끊김 관측", "외부 연결 끊김 추정" | M8BuilderTests.cs:229 |
| DiagnosisTexts.cs:16, :59, :61, :109; QuickDiagnosis.cs:261 | "어댑터 링크가 내려가 있음", "링크가 올라가 있고…" | link up/down 직역 | "네트워크 어댑터 연결(케이블·Wi‑Fi)이 끊겨 있음", "연결이 살아 있고 최근 30분 끊긴 기록이 없습니다" | 없음 |
| QuickDiagnosis.cs:287; DiagnosisTexts.cs:24 | "인터넷은 되지만 예외가 관측됐습니다 (공유기 무응답 또는 로그인 확인 응답)." | "예외" 전문 용어, 괄호 어색 | "인터넷은 되지만 확인할 점이 있습니다 (공유기가 Ping에 답하지 않았거나 로그인 페이지 같은 응답을 받음)." | 없음 |
| RecordsScreen.cs:570; RecordLibrary.cs:251; ReportBuilder.cs:112-113 | "끝 기록 없음" | 내부 용어 | "정상 종료 기록 없음" | M7RecordViewTests.cs:159, M8BuilderTests.cs:80 |
| RecordView.cs:86 | "마무리 기록을 쓰지 못함" | 같은 개념 다른 이름 | "정상 종료 기록을 쓰지 못함" | M7RecordViewTests.cs:162 |
| RecordsScreen.cs:620; MeasurementComparison.cs:60-61; RecordLocator.cs:55, :72 | "기록 머리를 읽지 못했습니다." 등 "머리" | 헤더 직역 | "기록의 기본 정보…" | M7LibraryTests.cs:209 |
| MeasureScreen.cs:311; PathMapModel.cs:78, :162 | "응답 기준선이 없는 대상", "응답 기준선 없음" | 전문 용어 (RecordView.cs:180은 "한 번도 응답하지 않은 대상") | "아직 한 번도 응답하지 않은 대상" | M5ModelTests.cs:365 |
| AppShell.Resident.cs:187 | "관측 {n}번. 측정 화면의 타임라인에서 확인하세요." | 잘린 문장 | "끊김이 {n}번 관측되었습니다. …" | 없음 |
| SettingsScreen.cs:125, :424 | "…끝까지 돕니다." | "돕다"로 읽힘 | "…끝까지 진행합니다." | 없음 |
| DiagnoseScreen.cs:71 (화면 읽기 이름) | "검사 일곱" | 어색 | "검사 7가지" | 없음 |
| ReportTexts.cs:28 | "…사업자는 서로 다른지만, 측정 ID는 시각 부분만…" | 잘린 표현, 어댑터 가린 사실 빠짐 | "사업자는 서로 다른지만 알 수 있게 '사업자 A·B'로, 어댑터는 종류와 순번으로, 측정 ID는 시각 부분만…" | 없음 |
| HomeScreen.cs:239 / DiagnoseScreen.cs:331, :435 / RecordsScreen.cs:1465 / DiagnosisView.cs:64 | "마지막 진단" / "마지막 검사" / "지난 진단" / "지난 검사와" | 섞임 | "지난 진단" | M6FlowTests.cs:146 |
| ReportBuilder.cs:200 | "하위 구간마다 아래 줄에" | 잘린 문장 | "하위 구간별로 아래 줄에 적음" | M8FlowTests.cs:179 |
| ReportBuilder.cs:117 | "관측 충족" | 어색 | "판단할 수 있는 관측 비율" | 없음 |
| RecordsScreen.cs:1376 | "관측하지 못한 시간이 많아 비교가 부정확합니다." | 단정 | "…부정확할 수 있습니다." | 없음 |
| Platform/LiveAppWorld.cs:46 (FirstRunScreen.cs:169); DetectionPlanner.cs:54 | "연결 정보를 읽지 못했습니다: " + 예외 형식 이름 | 영어 예외 이름 노출 | "연결 정보를 읽지 못했습니다. 다시 확인하세요." | 없음 |

## 낮음 (띄어쓰기·표기·작은 어색함)

| 위치 | 지금 | 제안 | 시험 |
| --- | --- | --- | --- |
| RecordsScreen.cs:1374 등 7곳 vs ProfileEditor.cs:291 등 | "응답 제한시간" / "제한시간" / "응답 제한" | "응답 제한 시간" | 없음 |
| QuickDiagnosis.cs:284, :304 vs :295 | "간헐 끊김이면…" vs "간헐적으로 끊긴다면…" | "가끔 끊긴다면 측정으로 기록하세요." | 없음 |
| ReportBuilder.cs:399 vs RecordsScreen.cs:806 | "이 측정 중에 한 빠른 진단이 없습니다." vs "돌린" | "실행한" | M8BuilderTests.cs:205 |
| RecordsScreen.cs:1058, :1066 vs ReportBuilder.cs:341-342 | "(측정 처음)/(측정 끝)" vs "(측정 시작)/(기록 끝)" | 한쪽으로 통일 | 없음 |
| ReportBuilder.cs:452; RecordView.cs:181 | "알림 {n}건" (기록 손상 안내) | "기록 문제 {n}건" | 없음 |
| ResidentNotifier.cs:162 | "관측이 이어진 시간 {n}초" | 분·초 형식 | 없음 |
| MeasurementAnalysis.cs:339, :341 | "외부 대상의 응답 손실이 7.3%로 많습니다." | "…7.3%(100번 중 약 7번)로…" | 없음 |
| HomeScreen.cs:322 (화면 읽기 이름) | "연결 품질 " + 등급 | "등급 " | 없음 |
| HomeScreen.cs:382 vs RecordsScreen.cs:554 | "분석 중" / "분석 중…" | 하나로 | 없음 |
| SettingsScreen.cs:250 vs DiagnoseScreen.cs:306 | "다시 시도하세요." / "다시 시도해 주세요." | 하나로 | 없음 |
| ResidentNotifier.cs:166-167 등 | "관측되었습니다" / "관측됐습니다" | 하나로 | 없음 |
| SettingsScreen.cs:299 vs :398 | 괄호 앞 띄어쓰기 섞임 | 하나로 | 없음 |
| SettingsScreen.cs:422 | "…새 판이라…" | "새 버전이라" | 없음 |
| MeasureScreen.cs:549 | "저장소가 회복되면 기록에 씁니다." | "저장할 수 있게 되면 기록에 씁니다." | 없음 |
| RecordsScreen.cs:423, :462 | "(줄을 열면 폴더를 열어 볼 수 있습니다)", "…줄을 펼치면 주변 회차" | "(항목을 열면 폴더를 볼 수 있습니다)", "줄을 펼치면 주변 회차가 보입니다" | 없음 |
| RecordsScreen.cs:829 (화면 읽기 이름) | "가장 높은 창 {ms}" | "5분 구간 중 가장 높은 값" | 없음 |
| RecordsScreen.cs:1340 | "표시 임계(…)" | "표시 기준(…)" | 없음 |
| RecordFolderRules.cs:56, :58 | "{name}와(과) 같은 폴더", "{name}를 품은 폴더" | "{name}와 같은 폴더", "{name}를 포함하는 폴더" | 없음 |
| RecordFolderRules.cs:31 | "바로 가기 연결(정션·심볼릭 링크)…" | "연결 폴더(정션·심볼릭 링크)…" | 없음 |
| ReportTexts.cs:81 | 세미콜론 | 쉼표나 두 문장 | 없음 |
| ReportTexts.cs:95-96, :114-115 | "관측했으나 이벤트 없음", "수집 미지원", "수집 능력: 접근 거부" | "관측했지만 끊김 기록 없음", "이 어댑터는 기록을 지원하지 않음" | M8BuilderTests.cs:124, M8FlowTests.cs:187 |
| SettingsScreen.cs:117-120; QuickDiagnosis.cs:319 | "외부 TCP 주소 1", "외부 TCP 443" | "확인할 외부 주소 1", "외부 연결(TCP 443)" | 없음 |
| RecordReader.cs:75 (홈 최근 목록) | "measurement.json이 없거나 읽을 수 없습니다." | "기록의 기본 정보 파일이 없거나 읽을 수 없습니다." | 없음 |
| FirstRunScreen.cs:182 | 로컬 대상 "{이름} {주소}" (캡처 공유 시 사설 공유기 주소) | 이름만 또는 그대로 | 없음 |
| SettingsScreen.cs:75 | 카드 제목 "보기" | "화면 표시" | 없음 |
| DiagnoseScreen.cs:110, :239, :244 | "…다시 돌립니다." | "다시 실행합니다" | 없음 |

## 화면 사이 용어 불일치 요약

- 끊김 횟수 단위: 홈·측정 "회", 기록·비교·리포트 "번".
- 응답 없음 계열: "응답 없음", "손실", "실패"가 섞임. 홈 숫자 줄만 "응답 성공 %".
- 끊김과 단절: 화면은 "끊김", 판정 이름은 "단절". "중간에 끊김"(측정 중단)이 네트워크 끊김과 충돌.
- 공유기·게이트웨이·로컬 대상이 섞임.
- 측정 구성과 프로필이 섞임.
- 원인 후보 표현: "가능성이 높은 곳", "후보", "구간 후보".
- 진단 쪽: "마지막/지난 진단·검사", 측정의 "검사 오류"가 진단의 "검사"와 겹침.
- 기록 상태 내부 용어: "끝 기록", "마무리 기록", "끝 요약", "머리".
- 응답 제한 시간 표기 섞임.
