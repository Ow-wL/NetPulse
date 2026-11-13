# M1 설계안: 골격

- 브랜치: `loop/m1-skeleton` (base `loop/m0-decisions`)
- 기준: `docs/loop-plan.md` 8절 M1, "M1 설계에서 정할 운영 세부". D1 = WinUI 3 (`DECISIONS.md`).
- 이 마일스톤에는 제품 기능이 없다. 이후 모든 마일스톤이 기대는 구조, 빌드, 테스트, 자동화, 데모 모드, CI를 만든다.

## 1. 목표와 끝났다는 증거

| 목표 | 증거 |
| --- | --- |
| 새 솔루션 구조 | `dotnet build NetPulse.slnx`가 깨끗한 클론에서 성공 |
| 한 가지 빌드 절차 | `build/build.ps1`이 복원 → 빌드 → 테스트 → 단일 파일 배포 → 실행 파일 해시 기록까지 |
| 테스트 러너 | 순수 로직 테스트와 Windows 통합 테스트가 분리되어 돈다 |
| UI 자동화 기반 | 빌드가 만든 바로 그 실행 파일을 띄워 창을 찾고, 값을 읽고, 캡처하는 UI 테스트 하나 |
| 데모 모드 틀 | `--demo <시나리오>`로 합성 측정값을 입력 자리에 넣는 경로 (화면 표시는 최소) |
| CI | PR마다(쌓인 PR 포함) 빌드 + 로직 테스트 + 통합 테스트 |
| 정리 | 기존 소스를 `legacy/`로, `spikes/` 삭제 (이력에 남음) |
| 빈 앱 | 흑요석 바탕의 창이 뜨고 데모 모드에서 회차 번호가 올라간다 |

## 2. 솔루션 구조

```
NetPulse.slnx
Directory.Build.props        공통 설정 (언어 버전, nullable, 경고를 오류로, 결정적 빌드)
Directory.Packages.props     중앙 패키지 버전 관리
src/
  NetPulse.Core/             net10.0. UI·Windows 의존 없음. 모델, 측정 엔진, 시계·공급자 추상화, 데모 시나리오
  NetPulse.Platform/         net10.0-windows10.0.22621.0. ICMP, 이벤트 로그, 경로·인터페이스 조회의 실제 구현
  NetPulse.App/              WinUI 3 앱. 화면, 디자인 시스템(M2), 시작·수명주기
tests/
  NetPulse.Core.Tests/       순수 로직. 시계·공급자를 가짜로 바꿔 끼움. CI 필수
  NetPulse.Platform.Tests/   실제 Windows API에 닿는 통합 테스트 (루프백 Ping, 이벤트 로그 읽기). CI 필수
  NetPulse.UiTests/          빌드된 실행 파일을 띄우는 UI 자동화. 로컬 G3에서 실행, CI에서는 제외(아래 7절)
build/
  build.ps1                  한 가지 빌드 절차
  ui-test.ps1                G3 실행 (산출물 해시 확인 후 UI 테스트)
demo/
  scenarios/*.json           데모 시나리오
legacy/                      기존 앱 소스 (M10에서 삭제)
```

- **의존 방향**: App → Platform → Core. Core는 어디에도 의존하지 않는다.
- **측정과 화면의 분리** (M0 교훈): 측정 엔진은 Core에 있고 UI 스레드와 무관하게 돈다. 화면은 엔진 상태를 구독만 한다. 화면이 준비되기 전에 도착한 결과는 엔진이 보관하고, 화면은 준비되면 최신 상태를 한 번 받아 그린다. 화면이 숨거나 다시 만들어져도 엔진은 그대로다.
- **M1에서 Core에 넣는 최소한**: `IClock`(실제·가짜), `IProbe`(대상 하나를 한 번 재기), 회차 스케줄러(M0 스파이크의 루프에서 버그를 고친 형태: 일찍 깨면 다시 기다림, 0번부터, 건너뛴 회차 계산), 데모 시나리오 읽기·재생. 대상 모델과 통계·기록 형식은 M3다.

## 3. 패키지 (빌드 전용 포함)

| 패키지 | 버전 | 쓰는 곳 | 구분 |
| --- | --- | --- | --- |
| Microsoft.WindowsAppSDK | 2.6.5 | App | 제품 런타임 (D1 승인) |
| System.Diagnostics.EventLog | 10.0.3 | Platform | 제품 런타임 (D1 승인) |
| xunit.v3 | 4.0.3 | 테스트 | 빌드 전용 |
| xunit.runner.visualstudio | 4.0.1 | 테스트 | 빌드 전용 |
| Microsoft.NET.Test.Sdk | 18.12.0 | 테스트 | 빌드 전용 |

- UI 자동화는 .NET에 들어 있는 `System.Windows.Automation`(UIAutomationClient)을 쓴다. 외부 패키지 없음.
- 차트는 M2 설계에서 정한다 (M0 재현처럼 직접 그리는 쪽이 유력).

## 4. 빌드 절차 (`build/build.ps1`)

1. 작업트리 상태와 HEAD를 기록한다. 커밋되지 않은 변경이 있으면 경고하고 산출물에 "커밋과 연결 안 됨"을 적는다.
2. `dotnet restore` → `dotnet build -c Release` (경고를 오류로)
3. `dotnet test` (Core.Tests, Platform.Tests)
4. `dotnet publish` App: win-x64, 자체 포함, 단일 파일, `EnableMsixTooling`
5. 산출물을 `artifacts/<짧은 SHA>/`에 두고 `manifest.json`을 쓴다: SHA, 작업트리 깨끗함 여부, SDK 버전, 빌드 명령, 파일별 SHA-256, 테스트 결과 요약
6. 종료 코드: 어느 단계든 실패하면 0이 아니다. 단계마다 제한시간(빌드·테스트 각 5분, 기획서 4.9)

`artifacts/`는 `.gitignore`에 넣는다.

## 5. G1 → G3 산출물 인계

- `build/ui-test.ps1 -Artifacts artifacts/<SHA>`는 `manifest.json`을 읽고, 실행 파일의 SHA-256을 다시 계산해 일치할 때만 UI 테스트를 돌린다. 일치하지 않으면 실패.
- UI 테스트는 실행 파일 경로를 환경 변수로 받는다. 다른 경로의 실행 파일을 찾아 쓰지 않는다.
- UI 테스트 결과(통과·실패, 캡처 경로)를 `artifacts/<SHA>/ui-test.json`에 쓴다. `STATE.md`의 G3 통과 SHA는 이 파일의 SHA다.

## 6. UI 자동화 기반 (M1 범위)

- `NetPulse.UiTests`의 공통 도구: 실행(인수, 사용자 데이터 폴더를 테스트마다 새 임시 폴더로), 프로세스 ID로 창 찾기, AutomationId로 요소 찾기, 상태 기반 대기(값이 기대와 같아질 때까지, 단계별 제한시간 60초), 클라이언트 영역 캡처, 종료 코드 확인.
- **M0 교훈 반영**: 실행을 끝낸 프로세스가 완전히 끝날 때까지 기다린 뒤 다음 실행. 함수 이름이 셸 별칭과 겹치지 않게 (PowerShell 도구도 C# 테스트로 옮겨 이 문제를 없앤다).
- M1의 UI 테스트 하나: 빈 앱을 데모 모드로 띄워 창 제목, 회차 번호가 1 이상으로 올라가는지, 다크 바탕이 칠해졌는지(캡처의 평균 밝기), 창을 닫으면 종료 코드 0인지.
- AutomationId 규칙: 화면 요소는 `영역.요소` 형식 (`home.verdict`). 코드에서 만드는 요소도 붙인다.

## 7. CI (`.github/workflows/ci.yml`)

- 트리거: `pull_request` (base 브랜치 제한 없음 → 쌓인 PR에서도 돈다), `push` (`main`, `loop/**`).
- 러너: `windows-latest`. .NET 10 SDK 설치 단계에서 SDK 버전을 `global.json`으로 고정.
- 단계: 복원 → 빌드(경고를 오류로) → Core.Tests → Platform.Tests → 앱 배포(단일 파일이 만들어지는지까지).
- **UI 테스트는 CI에서 돌리지 않는다**: 호스트 러너의 대화형 데스크톱·GPU 상태를 믿기 어렵고, 실패해도 원인을 가리기 어렵다. G3은 로컬에서 돈다. 이 결정은 M11에서 다시 본다.
- Platform.Tests는 외부 네트워크를 쓰지 않는다 (루프백만). 이벤트 로그 읽기 테스트는 "읽었다 또는 명시적 실패 상태"를 검사해 러너 권한 차이에 흔들리지 않게 한다.
- CI 로그에 4.7절 금지 항목이 찍히지 않게 테스트 출력에 주소·어댑터 이름을 쓰지 않는다.
- 공개 전 검사(`privacy-scan.ps1`)는 로컬 단계다 (이 PC의 값을 읽어야 하므로 CI에서는 의미가 없다).

## 8. 데모 모드

- 실행 인수 `--demo <이름 또는 경로>`. 이름이면 실행 파일 옆 `demo/scenarios/<이름>.json`.
- 데모는 `IProbe` 자리에 들어간다 (기획서 4.3: 가장 바깥에서만 바꿔 끼운다). 스케줄러·집계·저장·화면은 제품 코드를 그대로 탄다.
- 시나리오 형식 (M1은 이 정도, M3·M4에서 근거 수집 등을 더한다):

```json
{
  "version": 1,
  "name": "steady",
  "seed": 11,
  "targets": [
    { "id": "gateway", "role": "local", "address": "192.0.2.1" },
    { "id": "ext-a", "role": "external", "address": "198.51.100.10" }
  ],
  "segments": [
    { "from": 0, "to": 600, "target": "*", "rtt": { "base": 18, "jitter": 6 } },
    { "from": 120, "to": 126, "target": "ext-a", "result": "lost" }
  ]
}
```

- 주소는 문서용 범위(192.0.2.0/24, 198.51.100.0/24, 203.0.113.0/24)만 허용한다. 읽을 때 검사한다.
- 같은 시드는 같은 결과를 낸다 (테스트에서 확인).
- 데모 모드에서는 시계도 바꿀 수 있다: `--demo-speed 10`이면 10배속 (장시간 시나리오 검증용, M3에서 사용).

## 9. 운영 세부 (기획서 8절 "M1 설계에서 정할 운영 세부")

**기록 커밋 시점과 SHA 구분**

1. 코드 커밋 X에서 G1~G3을 돌린다 (`build.ps1`, `ui-test.ps1`).
2. G4 리뷰는 `base..X`를 본다.
3. G4 `APPROVE` 뒤 `STATE.md`·`DECISIONS.md`를 갱신한 기록 전용 커밋 Y를 만든다. Y는 `docs/loop/` 아래만 바꾼다.
4. 푸시하는 브랜치 끝은 Y, PR 본문의 "승인받은 코드 SHA"는 X다.
5. 기록 전용 판별: `git diff --name-only X..Y`가 모두 `docs/loop/`로 시작하면 기록 전용이다. 마감 스크립트가 검사한다.

**중복 PR 방지와 PR 번호 기록**

- PR을 만들기 전에 `gh pr list --head <브랜치> --state all`로 확인한다. 있으면 새로 만들지 않는다.
- PR 번호는 다음 마일스톤의 첫 커밋에서 `STATE.md`에 적는다. M11처럼 다음 마일스톤이 없으면 PR을 만든 뒤 기록 전용 커밋을 하나 더 만들어 푸시한다 (강제 푸시 없음).

**마감 스크립트** (`build/close-milestone.ps1`): 기록 전용 판별 → 커밋 단위 공개 전 검사 → PR 본문 검사 → 푸시 → 중복 확인 후 PR 생성. 실패하면 멈춘다. 사람이 하던 순서를 한 곳에 모아 빠뜨리지 않게 한다.

## 10. 정리

- 기존 소스(`*.cs`, `Dashboard.xaml`, `Build.ps1`, `App.manifest`, `NetPulse.ico`)를 `git mv`로 `legacy/`에 옮긴다. `fonts/`는 제품에서 쓰므로 `assets/fonts/`로 옮긴다. `README.md`는 루트에 두고 "재작성 중" 안내를 맨 위에 덧붙인다.
- `spikes/`를 지운다. M0 결정의 근거는 `DECISIONS.md`에 커밋 SHA로 연결되어 있어 이력에서 찾을 수 있다.
- `.gitignore`의 `*.csv` 전체 제외에 테스트 픽스처 예외(`!tests/**/fixtures/*.csv`)를 둔다 (기획서 2절).

## 11. 테스트 계획 (M1)

| 테스트 | 종류 | 무엇을 |
| --- | --- | --- |
| 스케줄러 | Core | 가짜 시계로: 0번부터 시작, 일찍 깨면 같은 회차를 두 번 내지 않음, 늦으면 건너뛴 회차를 셈, 취소하면 멈춤 |
| 시나리오 읽기 | Core | 버전, 필수 항목, 문서용 주소 외 거부, 겹치는 구간의 우선순위, 같은 시드 같은 결과 |
| 데모 공급자 | Core | 시나리오 구간대로 성공·손실·지연을 냄 |
| 루프백 Ping | Platform | 비관리자로 성공 또는 명시적 실패 |
| 이벤트 로그 읽기 | Platform | 읽음 또는 명시적 실패 상태 |
| 빈 앱 | UI | 6절 |

## 12. 위험

| 위험 | 대응 |
| --- | --- |
| WinUI 단일 파일 배포가 CI 러너에서 다르게 동작 | CI는 배포 산출물이 만들어지는지까지 본다. 실행 확인은 로컬 G3 |
| CI 러너의 .NET 10 SDK 버전 차이 | `global.json`으로 고정, `rollForward: latestPatch` |
| 쌓인 PR에서 base 브랜치 CI가 안 돌 수 있음 | `pull_request` 트리거에 base 제한을 두지 않는다. 첫 PR에서 실제로 도는지 확인 |
| xUnit v3과 WinUI 대상 프레임워크의 충돌 | Core.Tests는 net10.0, Platform.Tests는 net10.0-windows. App은 테스트하지 않는다 (UI 테스트가 맡음) |
| 기존 소스 이동으로 README의 빌드 안내가 틀어짐 | README 맨 위에 재작성 중임과 기존 앱 빌드 경로(`legacy/Build.ps1`)를 적는다 |

## 13. 이 마일스톤에서 하지 않는 것

대상·프로필 모델, 통계, 기록 형식(M3), 근거 수집(M4), 디자인 시스템과 화면(M2·M5). 빈 앱 화면은 흑요석 바탕색과 제목, 회차 번호 하나뿐이다.
