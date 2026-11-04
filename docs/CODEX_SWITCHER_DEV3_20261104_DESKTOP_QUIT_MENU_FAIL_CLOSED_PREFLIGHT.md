# Codex 계정전환 개발팀 #3 — Windows Desktop 정식 Quit 접근·Fail-closed 보완

작성시간: 2026-11-04 18:03:00 KST

## 상태

- `DESKTOP_CLOSE_ROOT_CAUSE_CONFIRMED`
- `STORE_MENU_QUIT_SOURCE_IMPLEMENTED`
- `OFFICIAL_CODEX_MENU_UNAVAILABLE_LIVE`
- `STORE_NO_MENU_FAIL_CLOSED_FIXTURE_PASS`
- `STORE_FILE_QUIT_FIXTURE_PASS`
- `LEGACY_NONSTORE_CLOSE_FIXTURE_PASS`
- `WINDOWS_DOTNET_BUILD_PASS_WARN0_ERR0`
- `WINDOWS_GUI_CHECKS_PASS`
- `WINDOWS_USAGE_MONITOR_196_OF_196_PASS`
- `OFFICIAL_LIVE_DESKTOP_NOT_TERMINATED`
- `A_ACCOUNT_CREDENTIAL_UNCHANGED`
- `SWIFT_CREDENTIAL_SWITCH_NOT_RUN`
- `CI_PENDING`
- `PRODUCTION_RELEASE_HOLD`

## 진단

- 개인 PC Windows Store `OpenAI.Codex` 설치 매니페스트에서 실제 `ChatGPT.exe` 경로 확인. 사용자 세션의 해당 공식 실행 파일은 **16개 프로세스**와 **1개 메인 창** 실행 중.
- 현재 앱 `DesktopController.CloseAsync`는 `CloseMainWindow()` (창 X와 같은 정상 창 닫기)를 보내고 관련 프로세스가 모두 사라지기를 최대 30초 기다림.
- 공식 openai/codex Windows 관련 이슈: `https://github.com/openai/codex/issues/49483` 및 `https://github.com/openai/codex/issues/50529`. 창 X는 닫히거나 숨겨지지만 백그라운드 프로세스가 남을 수 있고, **File → Quit ChatGPT**는 정상 종료 사례가 있음. 설치 버전마다 다를 수 있음.
- 사용자 실제 실행 Codex 창을 Windows `GetMenu` + UI Automation으로 **읽기 전용 검사**: native menu false, `MenuItem` 0, 메뉴/버튼의 File/Quit/Exit 접근성 후보 0 (전체 UIA descendant 12). 그러므로 현재 빌드에 대해 정식 Quit 메뉴의 프로그램식 접근은 **아직 불가능**. SendKeys/WM_CLOSE/Alt+F/강제 Kill/RestartManager shutdown **실행하지 않음**.
- 기존 A active SHA와 두 등록 계정 intact. 사용자의 실제 Codex Desktop 프로세스 종료·창 숨김·A→B 전환 명령 **0회**.

## 수정

1. `windows/CodexAccountSwitcher.Core/DesktopQuitMenu.cs` 추가:
   - 확인된 공식 Store 앱 프로세스 메인 창 UI Automation에서 정확히 File 메뉴를 찾고, 정확한 이름의 Quit ChatGPT / Quit Codex를 Invoke하는 경우만 성공.
   - 임의 입력·클립보드·정규 키보드 전송 없음. 다른 프로세스의 창 접근 없음. 메뉴가 없으면 false.
2. `windows/CodexAccountSwitcher.Core/DesktopController.cs`:
   - Store AUMID가 있는 앱이면 `CloseMainWindow` 절대 사용하지 않음.
   - 접근성 정식 Quit 명령이 없으면 기존 앱 창을 숨기거나 크레덴셜을 만지기 전에 즉시 IOException. `File → Quit ChatGPT` 수동 종료 후 재시도 안내.
   - 실제 Quit 후에는 모든 설치 경로/AUMID 매칭 프로세스 종료 확인(기존 30초).
   - 일반 비Store 앱은 기존 `CloseMainWindow` 방식 보존.
3. `windows/CodexAccountSwitcher.Core/CodexAccountSwitcher.Core.csproj`: WPF UI Automation framework reference 추가(`<UseWPF>`를 Core에 추가하면 SDK 글로벌 using 89개 오류가 발생해 사용하지 않고 명시적 `FrameworkReference`만 적용).
4. `windows/CodexAccountSwitcher.CloseFixture/Program.cs`: 격리 전용 Store-like 정상 종료 메뉴 모드 + X hide 모드 추가.
5. `windows/CodexAccountSwitcher.Checks/DesktopCloseChecks.cs`: 기존 nonStore 창 종료, Store 경로 재탐색, 접근 가능한 File/Quit 정상 종료, 접근 불가 시 창/프로세스 보존을 검증.
6. `windows/README.md`: Store hide-to-background 위험, 메뉴를 호출할 수 없을 때 전환 보류, 수동 Quit 재시도 절차 설명.

## 로컬 검증

- `dotnet build windows/CodexAccountSwitcher.CloseFixture -c Release`: PASS warning0/error0.
- `dotnet run --project windows/CodexAccountSwitcher.Checks -c Release -- desktop-close windows/CodexAccountSwitcher.CloseFixture/bin/Release/net10.0-windows/ChatGPT.exe`: PASS.
- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release`: `MONITOR_SYNTHETIC_PREVIEW_PASS checks=196 realCodexReads=0 actualAccountSwitches=0 telegramSends=0`.
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-graceful-quit-20261104`: PASS.
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release`: PASS 0 warnings/0 errors.
- `git diff --check`: PASS.

## 잔여 사항 및 판단

- **실제 Codex Desktop 자동 종료는 아직 PASS가 아님.** 현 버전의 공식 앱 메뉴가 UI Automation으로 보이지 않아 위 구현은 현재 사용자 환경에서 조기 중단 + 수동 `File → Quit ChatGPT` 안내가 주된 개선.
- 이 범위에서 실제 Store 앱을 종료하거나 재시작하지 않았고, 실제 A↔B↔A 전환은 수행하지 않음.
- 향후 Codex 제공자의 공식 종료 API/접근 가능한 앱 메뉴 제공 여부 검토. 사용자 작업을 위험하게 하는 전역 키보드 입력·강제 종료를 임의로 추가하지 않음.
- GitHub CI에서 Windows fixture + Swift core + 포터블 EXE 확인 후 새 Switcher 앱을 업데이트할 수 있음. 기존 버전 EXE 보존.
- 사용자 루트 README 개인 수정 및 미추적 기존 문서 전부 보존. 개인 Fork feature branch 외 수정 없음.
- macOS 실제 장비, Windows 실제 재부팅/멀티모니터/작업표시줄 자동숨김, 공식 릴리스 별도 Gate 유지.

다음 수행 주체: Codex 계정전환 개발팀 #3 → 범위 한정 Commit/Push → GitHub Windows CI PASS → 신 EXE isolated self-test → 안전 설치 및 A 유지 검증.
