# M10 설계 — 릴리스 준비

1판 · 2026-11-20 · 브랜치 `loop/m10-release` (`main` `3ed5671` = M9 병합 위)

기획서 8절 M10 행: "버전 표시, 배포 빌드 절차, README와 화면 캡처, `legacy/` 삭제". 끝났다는 증거: 깨끗한 클론에서 배포 파일 생성. 기준은 `docs/loop-plan.md` 1절 완성 정의 4("깨끗한 클론에서 문서화된 한 가지 절차로 배포 파일이 만들어진다"), 5절 "설치 없이"의 뜻(설치 프로그램 없음, Windows 11 기본 런타임만, 파일 하나든 폴더든 됨), 6절 R4(버전 표시, 배포 빌드 절차, README), 4.5(새 런타임 의존성·관리자 권한 없음, Release 게시는 사용자), 4.7(공개 기록 규칙), 이번 범위 밖(설치 프로그램, 자동 업데이트, 코드 서명).

**사용자 결정 (2026-11-20, DECISIONS "M10 범위")**

| 쟁점 | 결정 |
| --- | --- |
| 버전 번호 | **1.0.0** |
| 저장소 라이선스 | **MIT**, 저작권자 `Ow-wL` |
| 배포 파일 | **zip 하나를 만들고, Release 화면에는 exe만 따로도 올린다** (게시는 M11 인수 뒤 사용자 승인) |
| 배포 크기 | 쓰지 않는 Windows App SDK 구성요소(AI·ML·검색·위젯)를 빼서 **줄인다** (같은 2.6.5에서 빼기만, 새 의존성 없음) |
| M9 이월 | **모두 넣는다**: G4 사소 2건, CI 디스크 시험 흔들림, 시험 공백 채우기, 실측·풍선 눈 확인 |

## 1. 목표와 끝났다는 증거

| 목표 | 증거 |
| --- | --- |
| **A** 버전 1.0.0을 한 곳에서 정하고 앱 안에 보인다 (설정 "정보" 카드, 기록 머리, 리포트) | G2 버전 문자열 시험, G3·G5 정보 카드·고지 대화상자 |
| **B1** 한 가지 절차 `./build/build.ps1`이 배포 파일(zip, exe, 해시 파일)을 만든다 | G1 산출물 `release/`, `release.json` |
| **B2** 배포 exe 하나만으로 모든 화면이 돈다 | G3 전체 UI 시험을 exe 하나만 있는 폴더에서 돌린다 |
| **B3** 배포 크기를 줄인다 | 지금 234 MB에서 얼마나 줄었는지 `release.json`에 기록. 시험 전부 통과 |
| **B4** 배포 파일에 이 PC의 경로·이름이 들어가지 않는다 | 빌드 단계의 이진 검사 (3절 B4) |
| **B5** 깨끗한 클론에서 같은 절차로 만들어진다 | CI(깨끗한 실행기)가 같은 스크립트로 배포 파일을 만들어 워크플로 산출물로 남김 + 로컬 깨끗한 클론 확인 기록 |
| **C** LICENSE, 제3자 고지, README(데모 캡처), `legacy/` 삭제 | 파일, 고지 일치 시험(G2), README 링크·이미지 검사 |
| **D** M9 이월 4건 | 3절 D의 시험들 |

## 2. 구조

| 곳 | 바뀌는 것 |
| --- | --- |
| `Directory.Build.props` | `Version` 1.0.0, `Copyright`, Release 구성에서 경로 정규화(`ContinuousIntegrationBuild`) 항상 |
| `Directory.Packages.props`, `src/NetPulse.App/NetPulse.App.csproj` | `Microsoft.WindowsAppSDK` 메타패키지 → 필요한 구성요소 패키지만. 배포 폴더로 `App.xbf`·`.pri`를 복사하던 대상은 exe 하나 시험이 통과하면 뺀다 |
| `src/NetPulse.App/AppInfo.cs` (새) | 버전·커밋을 읽는 한 곳 |
| `src/NetPulse.App/Screens/SettingsScreen.cs` | "정보" 카드, 고지 대화상자 |
| `src/NetPulse.App/Legal/` (새, 내장 리소스) | `LICENSE.txt` 사본, `THIRD-PARTY-NOTICES.txt` |
| `build/build.ps1`, `build/common.ps1` | 묶기 단계(`release/`), 이진 검사, 고지 일치 검사 |
| `build/notices.ps1` (새) | 고지 파일 만들기·검사 |
| `.github/workflows/ci.yml` | `release/` 파일을 워크플로 산출물로 올림 |
| `tools/loop/clean-clone.ps1` (새) | 로컬 깨끗한 클론 확인 |
| `LICENSE`, `README.md`, `docs/images/` | 새로 쓰기 |
| `legacy/` | 삭제 |
| 시험 | 3절 D, 6절 |

## 3. 설계

### A. 버전과 정보

- **한 곳**: `Directory.Build.props`의 `Version`이 1.0.0. 파일 버전 1.0.0.0, 제품 버전 `1.0.0+<커밋 전체 SHA>`(SDK가 SourceLink로 붙인다, 지금 배포본에서 확인함: `0.5.0+762addc…`). `Copyright`는 `Copyright (c) 2026 Ow-wL`.
- **`AppInfo`**: `AssemblyInformationalVersionAttribute`를 읽어 `Version`(+ 앞), `Commit`(+ 뒤 앞 7자, 16진수가 아니면 없음)을 낸다. 지금 `typeof(App).Assembly.GetName().Version`을 쓰는 세 곳(측정 호스트의 기록 머리, 쇼케이스 로그, 리포트 화면)을 `AppInfo.Version`으로 바꾼다. 기록 머리의 앱 버전이 `0.5.0.0` 같은 네 마디에서 `1.0.0` 세 마디가 된다. 리포트의 `SafeVersion`은 1~4마디를 받으므로 옛 기록도 그대로 읽힌다(G2 시험으로 확인).
- **"정보" 카드**: 설정 화면 마지막 카드. 줄: 버전 `1.0.0`, 빌드 `abc1234`(없으면 "알 수 없음"), 라이선스 "MIT", 단추 [라이선스와 고지 보기]. 데모 실행에서는 빌드 줄이 "데모 실행"이다(캡처가 커밋마다 바뀌지 않게).
- **고지 대화상자**: 앱에 내장한 `LICENSE.txt`와 `THIRD-PARTY-NOTICES.txt`를 읽기 전용 글 상자 하나에 보인다(스크롤, 키보드 선택·복사 가능, 닫기). 외부 브라우저·파일을 열지 않는다. 큰 글(약 150 KB 예상)을 한 번에 넣어 대화상자 열기가 느리면 고지를 구성요소별로 접는 목록으로 바꾼다 (bench로 판단, 5절).
- 창 제목·트레이 도움말에는 버전을 넣지 않는다 (지금 그대로).

### B. 배포 빌드

**B1 절차 하나**: `./build/build.ps1`(게이트는 `-Gate`)이 지금 단계(복원 → 빌드 → 시험 → 배포 → manifest) 뒤에 묶기를 한다. 결과는 `artifacts/<sha10>/release/`:

| 파일 | 내용 |
| --- | --- |
| `NetPulse-1.0.0-win-x64.zip` | `NetPulse.exe`, `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt`, `읽어보기.txt` |
| `NetPulse-1.0.0-win-x64.exe` | zip 안과 같은 바이트의 exe (Release 화면에 따로 올릴 것) |
| `SHA256SUMS.txt` | 위 두 파일의 SHA-256 |
| `release.json` | 버전, 원본 커밋, manifest 해시, 각 파일 크기·해시, exe 크기 비교(이전 234 MB) |

- 파일 이름의 버전은 `Directory.Build.props`에서 읽는다(MSBuild `-getProperty:Version`). 이름이 두 곳에서 따로 정해지지 않게 한다.
- zip은 .NET `System.IO.Compression`으로 만들고 항목 시각을 커밋 시각으로 고정한다 (같은 입력이면 같은 zip).
- `읽어보기.txt`: Windows 11 x64, 설치 없음, 코드 서명이 없어 SmartScreen 경고가 날 수 있음, 기록·설정 위치, 지우는 법, 관리자 권한 불필요.
- manifest는 지금처럼 `app/`의 파일 목록과 해시를 담고, `release.json`은 manifest 해시를 담아 둘을 잇는다. `close-milestone.ps1`은 `release.json`이 있고 X의 것인지 확인한다.

**B2 exe 하나**: 패키지 없는 단일 파일 배포에서 M0 때는 `App.xbf`와 앱 `.pri`가 배포 폴더에 복사되지 않아 직접 복사하는 대상을 두었다. 2026-11-20 조사: M9 배포본의 exe만 빈 폴더에 두고 데모로 실행하니 첫 실행 화면이 정상으로 그려졌다(창만 캡처로 확인). Windows App SDK가 `IncludeAllContentForSelfExtract`를 강제해 내용 파일이 exe 안에 들어가는 것으로 본다(추정). 그래서:
1. 복사 대상을 빼고 배포 폴더가 exe 하나(+ 같은 단일 파일이 풀지 못한 것이 있으면 그 목록)인지 본다.
2. 게이트 UI 시험은 그 폴더(`app/`)에서 돈다. 즉 **배포하는 exe 그대로 전체 UI 시험(768개 + M10)을 돌린다.**
3. 실패하면 복사 대상을 되돌리고 zip에 `.pri`·`.xbf`를 넣으며, "exe만 따로"는 사용자에게 다시 묻는다 (임의로 정하지 않는다).

**B3 크기 줄이기**: `Microsoft.WindowsAppSDK`(메타) 대신 `Microsoft.WindowsAppSDK.Base`, `.Foundation`, `.WinUI`, `.InteractiveExperiences`, `.DWrite`, `.Runtime` 중 빌드·실행에 필요한 것만 같은 2.6.5로 참조한다. `AI`, `ML`, `Search`, `Widgets`는 뺀다. 앱 코드는 이 넷의 API를 쓰지 않는다(검색으로 확인한 뒤 적는다). 빼고 나서 G1·G2·G3 전체를 돌린다. 하나라도 깨지면 원인을 기록하고 그 구성요소만 되살린다. 결과 크기는 `release.json`과 DECISIONS에 남긴다. (`PublishTrimmed`는 WinUI·리플렉션 위험이 커서 하지 않는다.)

**B4 경로·이름이 배포 파일에 들어가지 않게**:
- Release 구성에서는 로컬에서도 `ContinuousIntegrationBuild=true`로 소스 경로를 `/_/`로 바꾼다. 지금은 CI에서만 켠다.
- 빌드의 마지막 단계에서 `release/`의 exe와 zip 안 파일을 이진으로 검사한다: 이 PC의 사용자 이름, 컴퓨터 이름, 사용자 폴더 경로, 저장소 절대 경로, NuGet 캐시 경로를 UTF-8과 UTF-16LE로 찾는다. 걸리면 빌드 실패. 찾을 값은 실행 중 환경에서 읽고 산출물·로그에는 "n건, 종류"만 적는다(값을 적지 않는다). CI에서는 CI 실행기의 값을 찾는다.

**B5 깨끗한 클론**:
- CI: `build.ps1` 뒤에 `release/`의 zip·exe·해시·`release.json`을 `actions/upload-artifact`(다른 액션과 같이 메이저 버전 고정)로 올린다. 보관 14일. 비밀 값 없음. Release 게시는 하지 않는다.
- 로컬: `tools/loop/clean-clone.ps1 -Commit <sha>`가 저장소를 임시 폴더로 `git clone --no-local` 한 뒤 그 커밋에서 `build.ps1 -Gate`를 돌리고, 결과의 버전·파일 목록·zip 항목을 확인해 `.loop/clean-clone-<sha10>.json`에 남긴다(공개 안 함, 결과 요약만 DECISIONS). 저장소 산출물과 exe 해시가 같은지도 적는다. 같지 않아도 실패는 아니고 원인을 적는다(단일 파일 묶음의 결정성은 확인하지 않은 사실).

**고지 파일**:
- `build/notices.ps1 -Write`가 앱의 `obj/project.assets.json`에서 실제로 배포에 들어가는 패키지(런타임 대상)와 .NET 런타임 팩을 모아, 각 패키지 캐시의 `license.txt`·`LICENSE*`·`NOTICE*`·`THIRD-PARTY-NOTICES*` 글을 이어 `src/NetPulse.App/Legal/THIRD-PARTY-NOTICES.txt`를 만든다. 머리에 구성요소 이름·버전·라이선스 표시 목록, 이어서 원문. Pretendard OFL도 넣는다.
- 이 파일은 커밋한다(검토 가능). `build.ps1`은 `-Check`로 다시 만들어 커밋된 것과 같은지 본다. 다르면 실패(의존성이 바뀌었는데 고지를 안 고친 것).
- 고지 글에 경로를 넣지 않는다(패키지 이름·버전만).

### C. 문서와 정리

- **LICENSE**: MIT 원문, `Copyright (c) 2026 Ow-wL`. 앱 안 사본(`Legal/LICENSE.txt`)은 빌드 때 루트 파일을 링크해 내장한다(사본을 따로 두지 않음).
- **README** (한국어로 새로 씀): 무엇을 하는 앱인가, 주요 기능(측정·끊김 판정·진단·기록·리포트·상주), 화면 캡처, 받기와 실행(zip 또는 exe, 요구 사항, SmartScreen), 개인정보(기록은 내 PC에만, 관리자 권한·네트워크 설정 변경 없음, 리포트 가리기), 직접 빌드(`git clone` → `./build/build.ps1`, 필요한 SDK는 `global.json`), 개발(시험 명령, 루프 문서 링크), 라이선스(MIT, 제3자 고지, Pretendard OFL). 기존 앱 설명과 `legacy` 안내는 지운다.
- **화면 캡처**: `docs/images/`에 6장 안팎(홈·측정 중 타임라인·진단 결과·기록 상세·리포트 미리 보기·설정)을 다크로, 대표 1장은 라이트로도. 데모 모드, 100%, 같은 창 크기, 창만 캡처(PrintWindow). UI 시험 `ReadmeShotsTests`(`Kind=Docs`, 게이트 밖)가 찍고, 사람이 고른 것을 커밋한다. 장당 400 KB 이하(PNG 최적화는 .NET 기본 인코더 범위에서). 커밋 전에 Claude가 눈으로 보고, G4에 이미지로 첨부한다.
- **`legacy/` 삭제**: 12개 파일. 기획서의 `legacy` 언급은 기록이라 그대로 둔다. 저장소 다른 곳에서 참조가 없는지 `git grep`으로 확인한다(README만 있음, 위에서 고침).

### D. M9 이월

**D1 G4 4라운드 사소 2건**
1. 느린 닫기 시험: 시험 전용 `FaultMoveOps`가 `hold-long` 지연에 들어가는 순간 진단 값 `moveHoldEntered`를 1로 올린다(앱 진단 표에 시험 값으로 노출, `--move-fail`이 있을 때만). 시험은 `.moving` 폴더 대신 이 값을 기다린 뒤 닫는다.
2. `RecordFolderRules.Display`: 앞부분 비교와 자르기를 모두 `SafeFull`로 정규화한 문자열로 한다. 시험: `.`·`..`·`/`가 낀 표기에서 표시가 올바른지.

**D2 CI 디스크 시험 흔들림** (`M3IntegrationTests.FlushToDiskCostPerRoundIsMeasured`, PR CI에서 p95 86 ms로 한 번 실패, 같은 커밋의 push CI는 통과): 300번 쓰기를 최대 3번 재고, 한 번이라도 p95 ≤ 50 ms면 통과한다. 모든 시도의 p95·최대를 `flush-cost.json`에 남긴다. 근거: 이 시험은 "회차마다 flush 비용이 예산 안에 들 수 있는가"를 본다. 공유 실행기 디스크의 일시적 지연은 앱 비용이 아니다. 로컬도 같은 규칙(한 경로). 대안(CI에서 기준 완화)은 기준이 둘이 되어 쓰지 않는다.

**D3 시험 공백 채우기** (M9에서 코드로만 고친 것):
- 주입 수단 두 개를 더한다. 둘 다 `--demo`와 명시한 `--user-data`가 있을 때만 받는다(`FaultMoveOps`와 같은 규칙).
  - `--memo-hold <n>`: 메모 저장이 n번째 쓰기에서 멈추고, 시험이 진단 명령 `memo.release`로 풀 때까지 기다린다. 성공·실패를 고를 수 있다(`--memo-hold 1:fail`).
  - `--records-read-hold`: 기록 상세 열기가 읽기 직전에 멈추고 `records.release`로 풀린다.
- 시험 (`M9GapTests`, G3):
  1. M7 이월 2: 메모 저장 성공 완료 전에 다른 칸을 고치고 있으면, 저장 완료와 목록 갱신 뒤에도 초점이 그 칸에 남는다.
  2. M7 이월 3: 저장 시작 뒤 휠로 다른 곳을 보고 있으면 저장 완료가 스크롤을 끌어오지 않는다.
  3. 숨기기 직전에 시작한 상세 읽기가 숨긴 뒤 끝나면 그리지 않고(`frameHooks` 0), 다시 열 때 그 상세가 보인다.
  4. 상세를 읽는 도중 기록 옮기기가 끝나면 "여는 중"에 머물지 않고 새 위치에서 열린다.
  5. 숨겼다 다시 열면 메모 칸의 선택 범위가 돌아온다.
- 시험 전용 진단 명령은 데모·명시 사용자 폴더 조건에서만 받는다(실사용 경로에 영향 없음, G2로 확인).

**D4 실측과 풍선 눈 확인**: M9 설계 7.3의 `M9LiveRunTests`(`Kind=Live`, 공개 안 함)는 아직 없다. 만든다: 데모 옵션 없이 측정 시작 → 창 닫기(안내) → 숨김 1분 → 작업 표시줄 알림 영역에서 실제 아이콘을 UIA로 찾아 열기·메뉴 → 종료. 찾지 못하면 기록하고 사용자 눈 확인으로. 풍선은 사용자가 보는 앞에서 띄운다(`--live-balloon-test`: 실측 실행에서 시험 풍선 한 번, Live 시험에서만). 결과는 사용자 답과 함께 DECISIONS에 남긴다. 실제 IP가 찍히므로 캡처·로그는 `.loop/`에만 둔다.

## 4. 상태표

**정보 카드**: 보통(버전·빌드·라이선스·단추) / 빌드 모름("알 수 없음") / 데모("데모 실행").
**고지 대화상자**: 보통(긴 글, 스크롤) / 읽기 실패(내장 리소스를 못 읽음: "고지를 읽지 못했습니다. 배포 zip의 THIRD-PARTY-NOTICES.txt를 보세요.").

## 5. 성능

- 설정 화면 들어가기는 기존 nav bench 기준 유지(정보 카드 추가 뒤).
- 고지 대화상자 열기: 첫 프레임 50 ms 이하·33 ms 초과 2회 이하를 bench로 잰다. 넘으면 3절 A의 접는 목록으로.
- 시작 시간: exe 하나로 바꾸고 구성요소를 줄인 뒤 첫 창까지의 시간을 이전 배포본과 같은 방법으로 비교해 기록(기준 없음, 회귀면 보고).

## 6. 시험 계획

- **G2**: `AppInfo` 파싱(+ 있음·없음·16진수 아님), 기록 머리 버전 3마디·옛 4마디 리포트 읽기, `Display` 정규화, flush 시험 규칙, 시험 전용 주입 옵션이 데모 밖에서 거절되는지, 고지 일치(`notices.ps1 -Check`를 빌드 단계로).
- **G3**: 전체 UI 시험을 exe 하나 폴더에서. 새 시험 `M10AboutTests`(정보 카드 값, 대화상자 열기·키보드·닫기·초점 복귀), `M9GapTests` 5개, 느린 닫기 시험 수정.
- **G5**: 장면 `settings-about`, `settings-about-dialog`(다크·라이트 × 100·150%, 고대비 2종). 매트릭스에 더한다.
- **배포 검사**: `release/` 파일 존재, 이름의 버전 = `Version`, `SHA256SUMS.txt` 일치, zip 항목 4개, zip 안 exe = 따로 낸 exe, 이진 검사 0건.
- **실측**: D4 (게이트 밖, 사용자 함께).

## 7. 위험

| 위험 | 대응 |
| --- | --- |
| exe 하나에서 일부 컨트롤 스타일·리소스가 빠짐 | 전체 UI 시험을 그 폴더에서. 실패하면 3절 B2-3 |
| 구성요소를 빼면 빌드는 되지만 실행 중 특정 기능(알림 영역·DWrite 등)이 깨짐 | 전체 시험 + 실측 D4. 깨지는 구성요소만 되살림 |
| 고지 원문 누락·잘못된 라이선스 표시 | 고지는 패키지 캐시 원문을 그대로 쓰고 일치 검사. Codex 검토 |
| 배포 exe에 로컬 경로 | 경로 정규화 + 이진 검사 |
| README 캡처에 실제 정보 | 데모 모드만, 공개 전 검사 패턴(B2)을 캡처 UIA 글에도 |
| 메타패키지를 버리면 2.6.5 다음 갱신 때 구성요소 버전이 어긋남 | `Directory.Packages.props`에서 한 변수로 같은 버전을 쓴다 |

## 8. 이 마일스톤에서 하지 않는 것

GitHub Release 게시(M11 인수 뒤 사용자 승인), 코드 서명, 설치 프로그램, 자동 업데이트, 잘라 내기(`PublishTrimmed`), 다국어 README.
