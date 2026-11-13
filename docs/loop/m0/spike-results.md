<!-- spikes/tools/make-results.mjs 가 요약 JSON에서 만든 파일. 손으로 고치지 않는다. -->

| 항목 | 합격선 | A-net10 | A-net48 | C |
| --- | --- | --- | --- | --- |
| P1 창 안쪽 유리 | 패널 뒤가 흐려 보임 | 캡처 판정 | 캡처 판정 | 캡처 판정 |
| P2 프레임 (정지 장면) | 평균 55 fps↑, 95백분위 25 ms↓ | 164.9 fps / 6.2 ms | 165 fps / 6.2 ms | 165 fps / 6.2 ms |
| 　테마 반복 전환 중 (6초) |  | 164.3 / 6.2 | 164.2 / 6.2 | 164.8 / 6.2 |
| 　화면 반복 전환 중 (6초) |  | 163.5 / 6.2 | 163.5 / 6.2 | 164.5 / 6.2 |
| P3 CPU (10초, 전체 코어 기준) | 15%↓ | 6.89% | 6.61% | 2.18% |
| 　메모리 작업 집합 합계 (평균 / 최대) | 최대 400 MB↓ | 541.7 / 574.1 MB | 460 / 488.3 MB | 144.5 / 146.4 MB |
| 　(보조) 전용 작업 집합 (평균 / 최대) | 판정에 안 씀 | 186.6 / 212.6 MB | 141.8 / 167.2 MB | 39.5 / 40.8 MB |
| 　관측된 프로세스 수 |  | 7 | 7 | 1 |
| P4 측정 영향 | 실제 시각 60초 이상, 0번부터 중복·누락 0, 지연 최대 100 ms↓ | 75초, 0~75번 76회차, 중복 0, 누락 0, 최대 14.4 ms | 75초, 0~75번 76회차, 중복 0, 누락 0, 최대 25 ms | 75.01초, 0~75번 76회차, 중복 0, 누락 0, 최대 14.6 ms |
| P5 30초 숨김 후 (앱 안에서 잼) | 누락 0, 다시 보인 뒤 5초 안에 최신 회차 | 누락 0, 16.3 ms 만에 40번 표시 (복귀 시 최신 40) | 누락 0, 17.5 ms 만에 40번 표시 (복귀 시 최신 40) | 누락 0, 16.8 ms 만에 40번 표시 (복귀 시 최신 40) |
| 　(보조) 바깥에서 읽은 값 | 최신 회차 이상 | 40 (다시 보인 뒤 757 ms, 도구 지연 포함) | 40 (다시 보인 뒤 616 ms, 도구 지연 포함) | 40 (다시 보인 뒤 88 ms, 도구 지연 포함) |
| P6 비관리자 진단 API | 관리자 아님, 네 API 모두 결과 또는 명시적 실패 | 4/4 성공 | 4/4 성공 | 4/4 성공 |
| P7 배포 크기 (파일 수) | 300 MB↓ | 49.7 MB (4) | 1.1 MB (10) | 94 MB (3) |
| 　첫 실행 / 두 번째 실행 시작 | 두 번째 3초↓ | 1.04 / 0.86초 | 0.76 / 0.68초 | 1.77 / 0.51초 |
| 　쓰기 금지 폴더에서 시작 | 가능 | 가능 | 가능 | 가능 |
| P8 자동화 | 스크립트 하나로 끝까지 | 통과 | 통과 | 통과 |
| P9 대체 동작 | 설정 읽기 + 대체 화면 | 캡처 판정 | 캡처 판정 | 캡처 판정 |
| **자동 판정 (P2~P8)** |  | **불합격 (P3)** | **불합격 (P3)** | 통과 |

**측정 근거**

| 후보 | 측정 시각 | 소스 커밋 (작업트리) | SDK | 빌드 명령 | 실행 파일 SHA-256 |
| --- | --- | --- | --- | --- | --- |
| A-net10 | 2026-11-13T04:12:37.3271077+09:00 | `39c2612` (깨끗함) | 10.0.103 | `dotnet publish spikes\a-webview\SpikeA.csproj -c Release -f net10.0-windows -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o <out>` | `BBF382314CCB016A…` |
| A-net48 | 2026-11-13T04:09:19.1306291+09:00 | `39c2612` (깨끗함) | 10.0.103 | `dotnet publish spikes\a-webview\SpikeA.csproj -c Release -f net48 -o <out>` | `600FF4F9E41BC795…` |
| C | 2026-11-13T04:05:32.8103057+09:00 | `39c2612` (깨끗함) | 10.0.103 | `dotnet publish spikes\c-winui\SpikeC.csproj -c Release -r win-x64 --self-contained true -p:EnableMsixTooling=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o <out>` | `55D5B4E031F6B534…` |

WebView2 런타임: A-net10 157.0.4341.46, A-net48 157.0.4341.46
