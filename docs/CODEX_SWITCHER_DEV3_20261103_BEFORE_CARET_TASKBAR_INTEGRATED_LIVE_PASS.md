# Codex 계정전환 개발팀 #3 — Windows 11 ^ 바로 왼쪽 작업표시줄 직접 통합

작성시간: 2026-11-03 20:55:24 KST

## 최종 상태
- `TASKBAR_BEFORE_NOTIFICATION_AREA_IMPLEMENTED`
- `ISOLATED_INTEGRATED_TASKBAR_CHILD_LIVE_PASS`
- `RIGHT_EDGE_EQUAL_TRAY_LEFT_GAP_0PX`
- `NO_POPUP_WINDOW`
- `LOCAL_MONITOR_172_OF_172_PASS`
- `LOCAL_WPF_UI_PASS`
- `DOTNET_RELEASE_WARN0_ERROR0`
- `REAL_ACCOUNTS_NOT_CONNECTED`
- `PRODUCTION_NO_GO`

## 요구사항과 기술 근거
- 사용자가 첨부한 화면의 '^' **바로 왼쪽**에 `5H 85% | 7D 62% | R 2` 형태의 고정 상태 패널을 원하는 것으로 확정. 기존 '^' 안/오른쪽 NotifyIcon 3개 방식은 불일치.
- Windows 11은 서드파티 Taskbar Toolbar/Deskband를 공식 지원하지 않음:
  https://www.microsoft.com/en-us/windows/windows-11-specifications
- `Shell_NotifyIcon`은 알림 영역 아이콘 전용이며 원하는 왼쪽 별도 밴드 위치를 제어하지 않음:
  https://learn.microsoft.com/en-us/windows/win32/shell/notification-area
- 비공식 오픈소스 `Deskband11Lib.Wpf` 사용. MIT, NuGet 1.4.2 고정:
  https://github.com/airtaxi/Deskband11Lib
  https://www.nuget.org/packages/Deskband11Lib.Wpf/1.4.2
- `BeforeNotificationArea`와 `SetParent`/WS_CHILD/Win32 창 영역 제한을 사용. Explorer에 코드 주입·DLL 패치·재시작·정책 변경 없음.

## Windows 실기기 환경
- Windows 11 26H2 / OS build 26300.9711. 레지스트리의 ProductName은 Windows 10 Home으로 표기될 수 있으나 빌드 버전으로 Win11 판정.
- TaskbarAl=0 (왼쪽 정렬)
- 물리 디스플레이 2560×1600, DPI 144 (150%).

## 격리 단독 합성 POC — 성공
- 공개 원본 코드 SHA `3f60e539db94585034aa55df8b2bd7f29ea0300f` (Deskband11Lib, 개인 저장소 수정 없음).
- 독립 합성 POC: `windows/artifacts/taskbar-before-tray-proof` (gitignore된 로컬 시험자료).
- 5H85 / 7D62 / R2 합성값만 사용.
- 빌드: .NET 10 WPF, 경고0/오류0.
- 실행 시 `ATTACHED_NATIVE_CHILD=true`.
- 실제 DPI 인식 물리 픽셀: 작업표시줄 RECT [0,1528,2560,1600], 테스트 패널 RECT [1757,1528,2102,1582].
- 알림 영역 HWND의 비-DPI 인식 PowerShell 좌표 X1401 ×1.5≈2102, 패널 오른쪽과 일치.
- 시험 POC는 자동 종료, Explorer 재시작/시스템 설치 없음.

## 개인 Fork 계정전환 통합 앱 자체 — 격리 실기기 PASS
- 기존 App의 오른쪽 3개 NotifyIcon 생성 경로를 새 `TaskbarQuotaPanel` 생성 경로로 변경.
- WPF 계정관리 창과 동일한 프로세스에서 하나의 가로 패널 생성; 작업표시줄 자식 HWND로 전환한 후에만 Show (비연결 시 독립 팝업 금지).
- `PreferredWidth=230` DIP, `PreferredHeight=36` DIP, `TaskbarContentPlacement.BeforeNotificationArea`, primary monitor.
- 기존 `ActiveMonitorPublicationGate` 유지: 활성·확인된 계정의 데이터만, 전환/오류시 즉시 무효화, 관측 2분 이후 지연/6분 이후 UNKNOWN, 별도 데이터 조회 없음.
- 클릭 시 관리창 열기, 우클릭 Open/Quit 메뉴; Explorer 재시작/모니터 제거/DPI 이벤트에는 기존 호스트 Dispose 후 생성 재시도.
- 정상 앱 실제 계정 실행은 하지 않았으며 임시 `CODEX_HOME`/데이터 경로만 사용.
- 격리 실행 PID=4396: `NATIVE_CHILD=True POPUP=False`.
- WPF/Explorer 동일 Win32 좌표: CHILD_RECT=[1171,1019,1401,1055], TRAY_RECT=[1401,1019,1706,1067].
- **LEFT_OF_CARET_GAP_LOGICAL_PX=0, INTEGRATED_BEFORE_TRAY_LIVE_PASS.**
- 해당 PID 즉시 종료, 원래 A auth 존재, 개인 Fork 운영 store 부재 유지, 격리 HOME auth 부재.

## 독립 로컬 회귀
- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release` → `MONITOR_SYNTHETIC_PREVIEW_PASS checks=172 realCodexReads=0 actualAccountSwitches=0 telegramSends=0`
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-before-tray-integration` → PASS. 새 Panel이 일반 App 구성에 존재하고 기존 floating/overflow icons가 일반 실행 경로에서 제거됨.
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo` → PASS, 경고0/오류0.
- `git diff --check` → 0.
- 원본 루트 README 사용자 미커밋 변경 및 기존 미추적 파일 보존.

## 변경 파일 (정확히)
1. windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj
2. windows/CodexAccountSwitcher.UiChecks/CodexAccountSwitcher.UiChecks.csproj
3. windows/CodexAccountSwitcher/App.xaml.cs
4. windows/CodexAccountSwitcher/TaskbarQuotaPanel.cs (신규)
5. windows/CodexAccountSwitcher.UiChecks/Program.cs
6. windows/README.md
7. docs/CODEX_SWITCHER_DEV3_20261103_BEFORE_CARET_TASKBAR_INTEGRATED_LIVE_PASS.md (본 보고서)

## 보류 및 주의
- 이 기술은 **비공식 Windows taskbar child-hosting** 방식. 새 Windows 업데이트나 Explorer UI 변경으로 고장 가능. 공식 API 기능이라고 표현하지 말 것.
- 실제 B 무료 계정 로그인/등록, 일반 사용자 A 계정의 실사용량 표시와 계정전환, 운영 설치·자동시작, Explorer 강제 재시작, 모니터 물리 연결 변경은 아직 하지 않음.
- `PRODUCTION_NO_GO`: 실제 계정전환 복구 및 재부팅·DPI·다중 모니터 실사용 Gate 후 정식 Release 검토.
- 뒤쪽 기존 Native 3아이콘 클래스 소스는 격리/회귀 자료로 남지만 일반 앱에서는 인스턴스화하지 않음. 기존 legacy IntegratedMiniBarWindow 소스도 회귀용 잔존.
- 개인 Fork `feature/provider-extensibility` 브랜치만 Commit/Push. 업스트림/main/Release는 변경하지 않음.

## 다음 수행 주체
Codex 계정전환 개발팀 #3 → 지정 파일만 Commit/Push, GitHub CI 확인. 이후 사용자 승인 시 정상 앱 실행, 기존 A 등록, 무료 B 등록, 실제 전환/원복/사용량 검증.
