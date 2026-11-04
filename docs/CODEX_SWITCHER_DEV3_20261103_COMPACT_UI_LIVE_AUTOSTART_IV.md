# Codex 계정전환 개발팀 #3 — Compact Flat Taskbar UI 실제 적용 및 로그인 자동실행 검증

작성시간: 2026-11-03 23:56:44 KST

## 상태
- `COMPACT_FLAT_UI_LIVE_PASS`
- `EXE_SINGLE_FILE_PACKAGE_SELF_TEST_PASS`
- `BEFORE_CARET_TASKBAR_CHILD_PASS`
- `PERSISTENT_USER_INSTALL_PASS`
- `LOGIN_AUTOSTART_RUN_KEY_PASS`
- `SINGLE_INSTANCE_ACTIVATION_PASS`
- `ACCOUNT_MANAGER_HIDE_BACKGROUND_PASS`
- `LIVE_REAL_A_USAGE_PASS`
- `A_CREDENTIAL_SHA_UNCHANGED`
- `LIVE_REBOOT_TEST_NOT_RUN`
- `DIRECT_MOUSE_CLICK_TEST_UNCONFIRMED`
- `LATEST_GITHUB_CI_NOT_RUN`
- `PRODUCTION_RELEASE_NO_GO`

## UI Source
- Branch `feature/provider-extensibility`, Commit `aa6cc8594cef866ff37c84ecfc6a432224fc2c13`.
- TaskbarQuotaPanel: 기존 230×36 DIP 연보라색 라운드 배경 대신 166×30 DIP 평면/투명 패널; 라벨 대비/값 강조, 구분선 2개, hover 상태.
- Windows .NET Release 빌드 경고 0 오류 0, WPF GUI 시험 PASS, Monitor synthetic 172/172 PASS, `git diff --check` PASS.
- 최신 커밋에 관한 `gh pr checks 1` 출력: no checks reported; CI PASS 주장 금지.

## 안전한 설치 경로 및 실행
- UI를 포함한 단일 EXE 게시:
  `windows/artifacts/compact-flat-ui-20261103/Codex-Account-Switcher-windows-x64.exe`
- EXE SHA256: `0d4f07509f643c4ff9fd1c86cfd91289e7cae1098110ce6db4d404fdd9e71694`
- 크기: 108143244 bytes, 이전에 검증한 CI Swift runtime ZIP 내장.
- `--self-test` PASS: bundled .NET / extracted Swift runtime / shared host / isolated settings / local monitor ledger.
- Self-test 전후 실제 A auth SHA와 A/B registry SHA 불변.
- 고정 사용자 전용 설치:
  `%LOCALAPPDATA%\Programs\Codex Account Switcher Personal\Codex-Account-Switcher-windows-x64.exe`
- 기존 Windows 계정전환 앱 프로세스 PID 2456만 종료하여 PID 25308로 새 EXE 백그라운드 실행. 다른 Codex Desktop 또는 Explorer 프로세스는 종료/변경하지 않음.
- 기존 `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CodexAccountSwitcherPersonal` 자동실행은 이미 활성 상태였고 개발용 artifacts 경로를 가리키고 있었음. 설치된 새 EXE의 `"<exe>" --background` 경로로 업데이트 후 동일 문자열 조회 PASS.
- 새 설치 바이너리를 중복 시작하여 기존 single-instance mutex/reactivation 동작 확인: 실행 인스턴스 정확히 1개, 계정 관리 창 열림. 계정관리 창 Close 후 앱 프로세스 유지, 패널 유지.
- 이 확인은 실제 Windows 로그온 재실행/재부팅 시험이 아님.

## 실제 작업표시줄 검증
- 기존 환경: Windows 11 빌드 26300.9711, 물리 디스플레이 2560×1600, DPI 144(150%).
- Windows 작업표시줄의 `Shell_TrayWnd` 자식 HWND, `WS_CHILD=True`, `WS_POPUP=False`.
- UIA taskbar child: 166×30 logical DIP, 경계 `TrayNotifyWnd` 시작과 간격 0.
- DPI-aware 물리 캡처: Rect=[1853,1528,2102,1573], **249×45 physical px**.
- 화면 캡처에서 큰 카드 배경 없이 투명 평면형 5H/7D/R 및 얇은 구분선 두 개가 렌더됨을 확인. 캡처는 gitignored artifact `windows/artifacts/compact-flat-live-dpi-capture.png`, SHA256 `6732685418f0d63e75642cd41d70f08606700f83c3c4d272f1677be383d7edbc`.
- 실사용 A 사용량은 변동값으로 관찰: 최초 신 UI 5H 64% / 7D 76% / R 2, 마지막 확인 5H 58% / 7D 75% / R 2.
- Windows UIA에서 배경 자식 HWND 및 텍스트 3개 확인.
- 직접 물리 클릭을 위한 MCP Windows Desktop UI operator는 호출 EXECUTED였으나, 확인된 실제 커서 좌표가 명령 좌표와 일치하지 않음. 패널 클릭으로 계정관리 창이 열렸다고 주장하지 않음. 소스의 `MouseLeftButtonUp` handler 존재하며, 새 EXE 중복 실행 후 account manager 정상 표시·숨김은 확인. 사용자 화면에서 직접 클릭 시험 필요.
- 앱 설정에 추가 로그인·계정전환·리셋권 소비·자동 매수·Telegram 동작 없음.

## 보존 점검
- 개인 Fork 등록 계정 2개, 현재 활성 index=0 (A).
- 실제 active Codex auth SHA = 저장 A 사본 SHA, B와 다름.
- 기존 사용자 루트 README 수정사항과 미추적 계획/인수인계 파일 그대로 보존.
- 자동시작 등록 외 운영 변경 없음.
- 최신 Swift stale-error source 수정은 별도 Commit `bee3b01`에 있지만, 이번 포터블 EXE는 이전 검증 CI-host를 포함하므로 최신 Swift 변경이 바이너리 반영되었다고 주장하지 않음. 별도 CI/출시 Gate 필요.

## 다음
1. 사용자 직접 패널 클릭으로 계정관리 창 열림 확인.
2. Windows 실제 재부팅/로그온 후 패널 자동 복원 확인 (사용자 업무 종료 후 수행).
3. 무료 B 사용량 미제공 원인 및 Codex Desktop 완전 정상종료 지연 해결, Swift CI를 재개하고 새 합본 EXE 업데이트.
4. GitHub latest CI, 설치/롤백, DPI/멀티모니터 장기 테스트 완료 전 PRODUCTION RELEASE NO-GO 유지.
