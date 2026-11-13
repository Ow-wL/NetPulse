# M4 이 PC 실측 기록

링크 근거(R2) 수집 방식을 정하려고 이 PC에서 잰 값. 관리자 권한 없이 실행했다. 어댑터 이름·주소·MAC·SSID는 적지 않는다.

## 1. 설계 전 조사 (2026-11-13)

| 항목 | 결과 |
| --- | --- |
| 실행 권한 | 관리자 아님 |
| 측정 연결 | 유선 (무선 어댑터는 내려가 있음, 무선 서비스는 실행 중) |
| 읽을 수 있는 로그 | `System`, `Microsoft-Windows-NetworkProfile/Operational`, `Microsoft-Windows-WLAN-AutoConfig/Operational`, `Microsoft-Windows-NCSI/Operational`, `Microsoft-Windows-Dhcp-Client/Admin`, `Microsoft-Windows-Wired-AutoConfig/Operational` |
| 이벤트가 없던 로그 | `Microsoft-Windows-Dhcp-Client/Operational`, `Microsoft-Windows-NDIS/Operational` (지난 30일 0건) |
| 유선 드라이버 링크 이벤트 (`System`, Intel 계열 유선 드라이버 두 종류) | 지난 30일 링크 다운(Id 27) 46건, 업(Id 32) 41건. 최근 열흘 중 하루 15회 |
| 다운 → 업 간격 (전체 기간 157쌍) | 최소 0.17초, 중앙 3.4초, 최대 110초 |
| 드라이버 이벤트의 어댑터 식별 | 이벤트 데이터 3개 중 하나가 어댑터 설명 문자열. 장치 경로·GUID 없음 |
| 무선 끊김·연결 이벤트 (8003, 8001) | 필드에 인터페이스 GUID 있음 |
| `NetworkProfile` 10000/10001 | 네트워크 프로필 GUID·이름만, 인터페이스 식별 없음 |
| `GetAllNetworkInterfaces` 비용 (PowerShell에서 50회) | p50 7.8 ms, p95 10 ms, 최대 22.6 ms |

**판단**: 1초 미만 끊김이 실제로 있으므로 1초 주기 조회만으로는 부족하다. 변경 알림 + 회차 확인을 기본 경로로, 로그를 보완 경로로 둔다. 회차마다 인터페이스 전체를 열거하지 않는다.

## 2. 통합 시험 결과

(구현 뒤 채운다: `GetIfEntry2` 비용, 이 PC 수집 상태, 빠른 진단 실행 결과)
