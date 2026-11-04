# Codex 계정전환 개발팀 #3 — 디스플레이 목록 재열거 경계 보완

작성시간: 2026-11-03 08:04:43 KST

## 상태
- `WINDOWS_DISPLAY_REENUMERATION_GUARD_PASS`
- `WINDOWS_UI_NATIVE_SINGLE_DISPLAY_150PCT_PASS`
- `MONITOR_SYNTHETIC_133_OF_133_PASS`
- `WINDOWS_BUILD_WARN0_ERROR0`
- `GITHUB_D862123_CI_NPM_MACPREVIEW_PASS_WINDOWS_PENDING`
- `PRODUCTION_NO_GO`

## 기준
- 개인 Fork: https://github.com/sirious0911/codex-account-switcher
- 브랜치: `feature/provider-extensibility`
- 수정 전 HEAD: `d8621230ce77bae92e0b09b5a5f56cd18bebbfc9`
- 외부 원본 upstream, 개인 main, Release는 수정하지 않음.

## 정적 리뷰 발견사항
`windows/CodexAccountSwitcher/IntegratedMiniBarWindow.cs`:
- 기존 `BuildMenu()`가 `FormsScreen.AllScreens`을 열거하는 도중 `SelectedScreen`이 다시 `AllScreens`를 요청하고 `First`를 호출함.
- 연결 해제/재연결 중 두 조회 시점이 서로 달라지면 대상 이름이 없는 화면 열거를 반환하거나 0개 목록에서 `First` 예외가 발생할 수 있음.
- `Dock()` 또한 화면 존재 여부와 실제 선택에 별도 조회를 수행하므로 경계가 불안정함.
- Windows에서 디스플레이 구성 변경 메시지의 UI 재구성은 장치 전환 직후 실행될 수 있어 실제 동작에 영향을 주는 경계임.

## 보완
- `SelectScreen(FormsScreen[] screens)`를 도입해 호출자가 제공한 동일 목록에서 우선 선택 화면을 판정하며 화면 0개일 때 null로 정상 귀결.
- `BuildMenu`는 디스플레이 목록을 한번만 얻고, 선택 상태 확인에 같은 목록을 사용함.
- `Dock`는 단일 목록에서 대상 화면을 결정해 없으면 LastDock=null 후 안전 종료.
- `WM_DISPLAYCHANGE / WM_SETTINGCHANGE / WM_DPICHANGED` 수신 시 0개 디스플레이에서도 컨텍스트 메뉴를 최신 상태로 갱신 가능.
- 모니터 선택의 실제 WPF 테스트에 빈 목록 경계·일반 목록 경계 검사를 추가.

## 직접 검증
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-integrated-enumeration-review` PASS. 실제 화면 1대, DPI144(150%), 메뉴 선택·Native 창 좌표 작업영역 내부.
- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release` 133/133 PASS. realCodexReads=0, actualAccountSwitches=0, telegramSends=0.
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo` PASS, 경고 0/오류 0.
- 실제 모니터 물리 분리, 다중 모니터 혼합 DPI, Explorer 재시작은 수행하지 않음.
- 실제 로그인·계정변경·인증파일 접근·운영 설치·알림 송신 없음.

## GitHub CI 조회
조회 기준 HEAD `d862123`:
- CI SUCCESS.
- npm installer SUCCESS.
- macOS personal-fork preview build SUCCESS.
- Windows 워크플로 조회 시 IN_PROGRESS. 전체 4/4 PASS 판정 전 Windows 완료 확인 필요.

## 변경 파일
1. `windows/CodexAccountSwitcher/IntegratedMiniBarWindow.cs`
2. `windows/CodexAccountSwitcher.UiChecks/Program.cs`
3. 이 검증 보고서

## 잔여
- 신규 Commit Github Actions 최종 결과
- 실제 다중모니터 연결해제/재연결/혼합 DPI/Explorer 재시작/절전복귀 실기기
- 실제 A/B 계정 등록 및 A→B→A 복귀, 일반 앱 실행 등 권한 필요 운영 시험
- Windows 자동시작 및 macOS 실기기, Release

## 다음 수행 주체
Codex 계정전환 개발팀 #3 → 3개 파일만 개인 Fork 기능 브랜치 Commit·Push 및 SHA 확인; 최신 CI 최종 확인. 운영 NO_GO.
