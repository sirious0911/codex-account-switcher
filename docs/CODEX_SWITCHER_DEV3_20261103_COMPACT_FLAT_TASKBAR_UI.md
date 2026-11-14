# Windows Codex Account Switcher — 작업표시줄 UI 축소 및 구조 명확화

작성시간: 2026-11-03 23:21:56 KST

## 사용자 피드백
- 실사용 캡처에서 5H 81%, 7D 79%, R 2가 커다란 연보라색 라운드 박스 안에 나타나 기존 Windows 작업표시줄과 시각적으로 어울리지 않음.
- 계정전환 앱을 별도 실행해야 하는지, 작업표시줄 아이콘에 통합되는지 구조 확인 요청.

## 아키텍처 (실사용 확인)
- Windows EXE 한 프로세스가 공유 Swift CoreHost와 WPF 계정관리 창을 소유하며, 동일 프로세스에서 `Deskband11Lib.Wpf`의 `BeforeNotificationArea` taskbar child HWND를 생성함.
- 시작 후 계정관리 창은 닫아 숨길 수 있고, `^` 바로 왼쪽 작업표시줄의 사용량 패널은 계속 남음. 패널 클릭으로 계정관리 창을 다시 열어 A/B 전환.
- **일반 Windows NotifyIcon이 아니며** 작업표시줄을 비공식 SetParent 방식으로 확장하는 구조. EXE가 종료되면 패널도 사라짐.
- `NativeSettings.LaunchAtLogin`은 Windows HKCU Run의 `CodexAccountSwitcherPersonal` 항목으로 지원. 2026-11-03 조회 시 **AUTO_START_REGISTERED=False**, 앱 PID2456 실행 중. 사용자가 Settings에서 명시적으로 설정하지 않은 한 부팅 시 수동 EXE 실행 필요. 자동실행 설정은 이번 변경 범위에서 수행하지 않음.

## UI 개선 SOURCE
- `TaskbarQuotaPanel` 230×36 DIP → **166×30 DIP** (DPI 144, 150%에서 약 345×54px → 249×45px).
- 기본 연보라색 불투명 채움 제거 → 투명 평면형 텍스트, hover 시에만 아주 옅은 배경 표시.
- 5H/7D/R 라벨은 muted, 수치는 선명한 SemiBold. 짧은 구분선 두 개.
- 툴팁과 좌클릭 관리창/우클릭 종료 메뉴 유지. 2분/6분 프레시니스, 계정 유효성 차단, 백엔드 폴링 로직 무수정.
- 원래 230×36 크기를 하드코딩한 WPF UI 검증 갱신.
- Windows README에 단일 프로세스, 자동시작 옵션, 바뀐 UI 방식과 실제 개인 저장소 경로 정정.
- 현재 실행 중인 포터블 EXE는 **교체·종료하지 않음**. 실제 작업표시줄에는 이전 UI가 계속 표시되고, 새 외관은 소스에만 반영.

## 오프라인 검증
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo` PASS, warning0 error0.
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-compact-taskbar-20261103` PASS, 실기기 DPI144 및 1개 물리 모니터.
- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release` 172/172 PASS, 실제 계정 전환·Codex 조회 0회.
- `git diff --check` PASS. 기존 사용자 root README 및 미추적 파일 모두 보존.
- 운영 Windows 실기기에서 새 색상/정확한 글자 렌더링은 아직 별도 재실행 전 → **LIVE_VISUAL_UNVERIFIED**.

## 다음
- 지정한 4파일만 개인 feature branch에 Commit/Push; CI 결과 확인.
- 사용자 원하는 스타일에 대한 추가 피드백 후, 새 포터블 exe 빌드와 사용자 승인된 앱 재시작을 통해 실제 UI 확인.
- 자동시작은 사용자 선택 전 미등록 유지. GUI 전환·B 무료 사용량 미제공·Explorer 비공식 호스트 안정성 별도 검증 게이트 유지.
