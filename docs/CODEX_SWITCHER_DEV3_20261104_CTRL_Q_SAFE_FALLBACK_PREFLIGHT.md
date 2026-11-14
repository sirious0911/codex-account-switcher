# Codex 계정전환 개발팀 #3 — Windows Ctrl+Q 정상 종료 대안

작성시간: 2026-11-04 23:08:18 KST

## 검증된 새 사실

- 공식 ChatGPT Desktop commands 문서 https://learn.chatgpt.com/docs/reference/commands 는 Windows `Quit the app` 단축키를 **Ctrl+Q**로 명시한다. `Close current tab or window`의 Ctrl+W / Windows X와 구별된다.
- OpenAI Codex GitHub 이슈 https://github.com/openai/codex/issues/49483 및 https://github.com/openai/codex/issues/50529 에서는 Windows X가 창만 숨기고 백그라운드 프로세스가 남을 수 있으나 정식 File → Quit 명령으로 앱 전체가 종료된다는 사례가 있다.
- 사용자 PC 공식 Store `OpenAI.Codex` 설치는 현재 **16개 공식 패키지 프로세스와 메인 창 1개**로 실행 중. 기존에 File/Quit 메뉴를 UI Automation으로 노출하지 않는 것도 확인됨.
- 현재 계정전환 앱은 `graceful-quit-ci-20261104` 버전 1개, 자동실행 등록, 활성 A와 저장 A 인증 SHA 일치, 등록 A/B 2개.
- 실제 공식 Desktop에는 **Ctrl+Q를 전송하거나 종료 시도하지 않음**. 활성 작업이 있을 수 있으므로 실제 A→B→A도 수행하지 않음.

## SOURCE 수정

1. `windows/CodexAccountSwitcher.Core/DesktopQuitShortcut.cs` 신규.
   - 계정전환 UI에서 사용자가 실제로 전환 확인을 누른 이후, 이미 `DesktopController`에서 검증한 공식 Store 프로세스의 MainWindowHandle/PID만 대상으로 함.
   - `SetForegroundWindow` 성공 후 `GetForegroundWindow`와 `GetWindowThreadProcessId` 일치 조건을 반드시 검사한 다음에만 Windows 공식 **Ctrl+Q** 입력을 한 번 전송함.
   - 포커스 권한이 없거나 대상이 다르면 키보드 입력 0건 및 실패 반환. 임의 앱에 전역 단축키 입력, 프로세스 Kill, WM_CLOSE, 관리자 권한 상승 없음.
   - `SendInput` 부분 거부 시 키 up으로 modifier 정리.
2. `windows/CodexAccountSwitcher.Core/DesktopController.cs`: Store 앱은 1차 File→Quit UI Automation, 메뉴 미노출 시 2차 Ctrl+Q 표준 명령. 둘 다 거부 시 기존 app/자격증명에 변화 없이 수동 Ctrl+Q 후 재시도 안내. 실제 입력이 수락돼도 모든 공식 Desktop 프로세스가 종료할 때까지 확인하고 나서만 Swift 계정전환 진행. 기존 30초 대기는 유지.
   - 합성시험용 `gracefulExitTimeout` optional 인자로 2~5초 시험 허용, 실사용 기본 30초 유지.
3. `windows/CodexAccountSwitcher.CloseFixture/Program.cs`: 정식 Ctrl+Q 핸들러가 있는 가짜 Store 앱 모드 추가. 실제 Codex 패키지/인증 미사용.
4. `windows/CodexAccountSwitcher.Checks/DesktopCloseChecks.cs`: UI 메뉴 정상 종료, 메뉴와 단축키 미작동 시 fail-closed, Ctrl+Q synthetic dispatch 시험. Windows가 외부 비대화형 테스트 프로세스의 foreground 활성화를 거부하면 해당 keyboard 시험은 **SKIP**로 기록하고, 원본 fixture 창과 프로세스 보존을 확인함. SKIP는 자동 정상 종료의 실기기 PASS가 아니다.
5. `windows/README.md`: Windows 공식 Ctrl+Q, HWND/PID 확인 및 거부 시 수동 종료 설명.

## 로컬 시험 결과

- Windows .NET Release 빌드: **warnings 0, errors 0**.
- `dotnet run --project windows/CodexAccountSwitcher.Checks -c Release -- desktop-close windows/CodexAccountSwitcher.CloseFixture/bin/Release/net10.0-windows/ChatGPT.exe`:
  - 공식 패키지 경로/앱 ID 재탐색 합성 PASS.
  - File Quit 격리 정상 종료 PASS.
  - 메뉴가 없고 Ctrl+Q 입력이 거부된 fixture에서 창·프로세스 보존 PASS.
  - **Ctrl+Q 자동 Quit 시험은 Windows foreground focus 거부로 SKIP**. 키가 실제 공식 App에서 처리되는지는 미확인.
- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release`: **196/196 PASS**, 실계정 API 0, 실제 계정전환 0.
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-quit-shortcut-20261104`: PASS.
- `git diff --check`: PASS, CRLF 주의 메시지 외 오류 없음.
- 사용자 기존 root README 수정 및 개인 미추적 문서·스크립트 보존. 이 커밋에 절대 포함하지 않음.

## 제약/판정

- **자동 Ctrl+Q 실제 Windows Codex 전용 앱에서 성공 여부 미검증**. 실시간 작업이 진행 중일 수 있는 현재 Desktop에 종료 명령 전송하지 않음.
- `SetForegroundWindow` 정책은 호출 프로세스/포커스 상태에 따라 달라짐. 실사용 Switcher가 사용자 전환 확인을 받은 순간에는 성공 가능성이 있지만 임의 보장 금지.
- 이미 메인창을 숨긴 상태(백그라운드 프로세스 잔류)에서는 자동 Ctrl+Q를 보내지 않고 수동으로 앱 창을 복원/종료해야 함.
- CI에서 Swift 공용 코어/Windows fixture 검사 후 새 EXE isolated self-test PASS라면 기존 실행 Switcher만 재시작하는 조건으로 Windows 적용 가능.
- 실제 B 계정 30D 사용량 표시는 이전 단계 확인됐지만 B 활성 트레이 표시와 실제 안전한 A↔B→A 복귀는 아직 별도 Gate.
- 사용자 Desktop 실제 정상 종료, Windows 재부팅 후 자동실행, 다중모니터·auto-hide, macOS 실물, 정식 Release는 미검증.
- 작업창(Work Window) 사용 0 / Codex Desktop 강제 Kill 0 / 활성 계정 auth 변경 0.

STATUS:
SOURCE_CTRL_Q_FOCUS_GATED_IMPLEMENTED
LOCAL_DOTNET_AND_FIXTURE_PASS
CTRL_Q_REAL_FOREGROUND_TEST_SKIPPED
REAL_CODEX_DESKTOP_UNTOUCHED
PRODUCTION_NO_GO

다음 수행주체: Codex 계정전환 개발팀 #3 → 정확한 SOURCE·TEST·DOC 6파일 Commit/Push → GitHub Windows CI → 새 EXE 자체시험 → 현재 A 로그인 보존하며 Switcher 앱만 교체(필요 시) → 업무 종료 시 수동 참여를 통한 Ctrl+Q/A→B→A 실기기 최종 확인.
