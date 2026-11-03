# Codex 계정전환 개발팀 #3 — 작업표시줄 사용량 세로 중앙 정렬 실기기 완료

작성시간: 2026-11-04 00:32:20 KST

## 사용자 요구
- 사용자 제공 작업표시줄 실사진에서 `5H 50% | 7D 74% | R 2` 숫자가 ^ 아이콘 왼쪽에 있지만, 작업표시줄 상단에 지나치게 붙음.
- 가로 위치, 5H/7D/R 내용 및 기존 계정전환 흐름을 유지하면서 **Windows 작업표시줄 상하 가운데**로 정렬.

## 근본 원인
- Deskband11Lib의 `TaskbarLayoutCalculator.Calculate`는 별도 y 오프셋을 계산하지 않고 `rowRectangle.top - taskbarRectangle.top`로 호스트 HWND를 작업표시줄 row 상단에 배치.
- 기존 `TaskbarQuotaPanel`은 166×30 DIP HWND 자체를 상단에 배치했기 때문에 내부 `VerticalAlignment.Center`는 작업표시줄 중앙 기준이 아님.
- 실제 기존 DPI 150% 화면: taskbar top 1528, bottom 1600 (72px), panel top 1528, bottom 1573 (45px).

## 수정한 구현
- `TaskbarQuotaPanel.PreferredWidth` 166 DIP 유지.
- `PreferredHeight`를 30 DIP → **48 DIP**(현재 작업표시줄 row 높이)로 변경.
- `IndicatorHeight=30 DIP`를 별도 도입하고, 48 DIP 투명 `Grid taskbarCanvas`에 30 DIP `Border background`를 `VerticalAlignment.Center`로 배치.
- `TaskbarContentHost`와 `Window.Content` 모두 `taskbarCanvas`를 사용해 기본 SetParent/BeforeNotificationArea 위치는 변경하지 않음.
- 기존 가로 구분선/텍스트 색상/마우스 클릭/상태 게이트/코덱스 데이터/로그인/알림/자동실행 로직 수정 없음.
- Windows UiChecks의 패널 크기 및 중앙화 컨테이너 불변 테스트 갱신.

## 빌드·격리 시험
- `dotnet build windows/CodexAccountSwitcher/CodexAccountSwitcher.csproj -c Release --nologo`: PASS (warning 0, error 0).
- `dotnet run --project windows/CodexAccountSwitcher.UiChecks -c Release -- windows/artifacts/ui-centered-taskbar-20261104`: PASS.
- `dotnet run --project windows/CodexAccountSwitcher.MonitorChecks -c Release`: 172/172 PASS, realCodexReads=0 actualAccountSwitches=0 telegramSends=0.
- `git diff --check`: PASS.
- 새 포터블 단일 EXE 108143754 bytes; SHA256 `62a0ae190a437e5413954615533bf0ef987926e0267607ebc0d575f191be3144`.
- 새 EXE `--self-test`: PASS (bundled .NET, extracted Swift runtime, shared host, isolated settings/local monitor ledger).
- 원래 A 인증파일 및 A/B 계정 등록원장 SHA256 유지.

## 사용자 Windows 실기기
- 현재 설치 경로: `%LOCALAPPDATA%\Programs\Codex Account Switcher Personal\releases\centered-20261104\Codex-Account-Switcher-windows-x64.exe`.
- 기존 앱 PID25308만 종료, 신규 앱 PID **14512** 정상 실행.
- Windows `HKCU\Software\Microsoft\Windows\CurrentVersion\Run\CodexAccountSwitcherPersonal`이 새 경로의 `--background`를 가리키도록 갱신 확인. 이전 EXE는 롤백용으로 유지. Explorer/Codex Desktop 종료·계정전환 없음.
- 화면 배율 150%, 작업표시줄 물리 RECT [0,1528,2560,1600].
- 새 패널 물리 RECT **[1853,1528,2102,1600] = 249×72px**.
- `TrayNotifyWnd` 시작 X=2102, 패널 오른쪽 끝 X=2102. **^ 바로 왼쪽 간격 0px**.
- 실제 Windows UIA에서 **5H 33% | 7D 71% | R 2** 세 항목 확인(사용량은 갱신에 따라 달라짐).
- 150% DPI 화면 캡처: `windows/artifacts/centered-taskbar-live-dpi-20261104.png`; SHA256 `eb16804c0a616804df6e3d77c84ef9fcbd098d197bc52253f1fd29079e1d93cb`.
- 실캡처 색상 기반 글자 픽셀 범위: Y=31~42 (72px 패널 내 상대 좌표), 상단 여백 31px / 하단 여백 29px, **차이 2px**, `VISUAL_TEXT_CENTERED_PASS`.
- 패널은 작업표시줄 내부 child window이고 별도 popup은 없음.

## 미검증 경계
- 새 버전의 클릭 동작은 직접 GUI 클릭으로 검증하지 않았음(기존 클릭 핸들러 그대로 유지, 사용자가 이전 버전에서 클릭 정상 확인).
- 부팅/로그온 실기기 재시험 및 다중모니터/자동숨김/다른 작업표시줄 크기 적응은 미실시.
- 비공식 Taskbar SetParent API에 따른 Windows 업데이트 호환성 보장 불가.
- 기존 Codex Desktop 자동 종료 지연, 무료 B 사용량 미제공, Swift 최신 오류 정리 커밋 CI 미실행 문제는 별도 추적, 본 Gate에 포함되지 않음.
- 개인 fork `feature/provider-extensibility`만 변경, main/Release 불변, 원래 사용자 root README 및 미추적 기록 보존.

## 판정
VERTICAL_CENTER_SOURCE_PASS
DOTNET_LOCAL_REGRESSION_PASS
EXE_SELFTEST_PASS
TASKBAR_VERTICAL_CENTER_LIVE_PASS
A_CREDENTIAL_UNCHANGED
PERSONAL_AUTOSTART_PATH_UPDATED
PRODUCTION_RELEASE_NO_GO

다음 수행 주체: Codex 계정전환 개발팀 #3 → 지정 SOURCE/TEST/보고서 3개만 feature branch Commit/Push; 이후 사용자 시각 확인 및 남은 macOS/Swift 안정화 분리.
