<!-- spikes/tools/make-results.mjs 가 요약 JSON에서 만든 파일. 손으로 고치지 않는다. -->

| 항목 | 합격선 | A-net10 | A-net48 | C |
| --- | --- | --- | --- | --- |
| P1 창 안쪽 유리 | 패널 뒤가 흐려 보임 | 캡처 판정 | 캡처 판정 | 캡처 판정 |
| P2 프레임 (정지 장면) | 평균 55 fps↑, 95백분위 25 ms↓ | 165 fps / 6.2 ms | 165 fps / 6.2 ms | 165 fps / 6.2 ms |
| 　테마 반복 전환 중 (6초) |  | 164.3 / 6.2 | 164 / 6.2 | 164.8 / 6.2 |
| 　화면 반복 전환 중 (6초) |  | 163.5 / 6.2 | 163.7 / 6.2 | 164.8 / 6.2 |
| P3 CPU (10초, 전체 코어 기준) | 15%↓ | 6.07% | 6.27% | 2.1% |
| 　메모리 작업 집합 합계 (평균 / 최대) | 최대 400 MB↓ | 540.9 / 569.7 MB | 462.1 / 488.4 MB | 143.9 / 146.2 MB |
| 　(보조) 전용 작업 집합 (평균 / 최대) | 판정에 안 씀 | 185.3 / 210.5 MB | 145.4 / 167.7 MB | 39 / 40.5 MB |
| 　관측된 프로세스 수 |  | 7 | 7 | 1 |
| P4 측정 영향 | 60초 이상, 중복·누락 0, 지연 최대 100 ms↓ | 76초 76회차, 중복 0, 누락 0, 최대 15.3 ms | 76초 76회차, 중복 0, 누락 0, 최대 25.5 ms | 76초 76회차, 중복 0, 누락 0, 최대 15.3 ms |
| P5 30초 숨김 후 | 누락 0, 5초 안에 최신 회차 | 누락 0, 30 ms 만에 40 (복귀 시 최신 40) | 누락 0, 35 ms 만에 40 (복귀 시 최신 40) | 누락 0, 18 ms 만에 40 (복귀 시 최신 40) |
| P6 비관리자 진단 API | 네 API 모두 응답 | 4/4 | 4/4 | 4/4 |
| P7 배포 크기 (파일 수) | 300 MB↓ | 49.7 MB (4) | 1.1 MB (10) | 94 MB (3) |
| 　첫 실행 / 두 번째 실행 시작 | 두 번째 3초↓ | 0.88 / 0.86초 | 0.72 / 0.68초 | 1.76 / 0.53초 |
| 　쓰기 금지 폴더에서 시작 | 가능 | 가능 | 가능 | 가능 |
| P8 자동화 | 스크립트 하나로 끝까지 | 통과 | 통과 | 통과 |
| P9 대체 동작 | 설정 읽기 + 대체 화면 | 캡처 판정 | 캡처 판정 | 캡처 판정 |
| **자동 판정 (P2~P8)** |  | **불합격 (P3)** | **불합격 (P3)** | 통과 |

**측정 근거**

| 후보 | 측정 시각 | 소스 커밋 (작업트리) | SDK | 빌드 명령 | 실행 파일 SHA-256 |
| --- | --- | --- | --- | --- | --- |
| A-net10 | 2026-11-13T03:48:58.4972629+09:00 | `9540198` (깨끗함) | 10.0.103 | `dotnet publish spikes\a-webview\SpikeA.csproj -c Release -f net10.0-windows -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o <out>` | `6314047B69DBDCA1…` |
| A-net48 | 2026-11-13T03:45:39.8199818+09:00 | `9540198` (깨끗함) | 10.0.103 | `dotnet publish spikes\a-webview\SpikeA.csproj -c Release -f net48 -o <out>` | `A3B1860012D448A7…` |
| C | 2026-11-13T03:42:10.1964976+09:00 | `9540198` (깨끗함) | 10.0.103 | `dotnet publish spikes\c-winui\SpikeC.csproj -c Release -r win-x64 --self-contained true -p:EnableMsixTooling=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o <out>` | `94093E40411B037D…` |

WebView2 런타임: A-net10 157.0.4341.46, A-net48 157.0.4341.46
